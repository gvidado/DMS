using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TelemetryMonitor
{
    internal sealed class FlightSequenceRow
    {
        public int Row;
        public string Phase="", Action="", Parameter="", Reference="", Range="", Keyword="";
    }

    internal sealed class FlightSequenceSheet
    {
        public string Id=Guid.NewGuid().ToString("N");
        public string Name;
        public List<FlightSequenceRow> Rows=new List<FlightSequenceRow>();
    }

    public static partial class WorkbookReader
    {
        private sealed class FlightMerge
        {
            internal int Start, End;
            internal string Label;
        }

        internal static List<FlightSequenceSheet> ReadFlightSequences(byte[] bytes)
        {
            var result=new List<FlightSequenceSheet>();
            using(var memory=new MemoryStream(bytes))
            using(var zip=new ZipArchive(memory,ZipArchiveMode.Read))
            {
                var shared=SequenceSharedStrings(zip);
                foreach(var sheet in SequenceSheets(zip))
                {
                    var source=ReadFlightSheet(sheet.Key,sheet.Value,shared);
                    if(source!=null && source.Rows.Count!=0)result.Add(source);
                }
            }
            if(result.Count==0)throw new ArgumentException("未找到可转换的飞行判读表。请核对 A～G 列的表头和数据行。");
            return result;
        }

        private static FlightSequenceSheet ReadFlightSheet(string name,XDocument document,List<string> shared)
        {
            int count=0, headerRow=0;
            var cellsByRow=new SortedDictionary<int,Dictionary<int,string>>();
            foreach(var row in document.Root.Elements(S+"sheetData").Elements(S+"row"))
            {
                if(++count>10000)throw new ArgumentException("工作表“"+name+"”超过10000行");
                int number;
                if(!Int32.TryParse((string)row.Attribute("r"),out number))number=count;
                if(number<1||number>1048576||cellsByRow.ContainsKey(number))throw new ArgumentException("工作表“"+name+"”的原表行号无效");
                var cells=new Dictionary<int,string>();
                foreach(var cell in row.Elements(S+"c"))
                {
                    int column=Column((string)cell.Attribute("r")??"");
                    if(column<1||column>7||column==6)continue;
                    cells[column]=ReadFlightCell(cell,shared,column);
                }
                cellsByRow.Add(number,cells);
                if(headerRow==0&&count<=50&&IsFlightHeader(cells))headerRow=number;
                if(headerRow==0&&count==50)return null;
            }
            if(headerRow==0)return null;

            var definition=new FlightSequenceSheet{Name=name};
            var merges=FlightMerges(document,cellsByRow,headerRow);
            int phaseIndex=0,actionIndex=0;
            foreach(var pair in cellsByRow)
            {
                if(pair.Key<=headerRow)continue;
                var cells=pair.Value;
                var row=new FlightSequenceRow{
                    Row=pair.Key,
                    Phase=FlightLabel(pair.Key,FlightCell(cells,1),merges[0],ref phaseIndex),
                    Action=FlightLabel(pair.Key,FlightCell(cells,2),merges[1],ref actionIndex),
                    Parameter=FlightCell(cells,3),Reference=FlightCell(cells,4),
                    Range=FlightCell(cells,5),Keyword=FlightCell(cells,7)
                };
                // A merged heading alone does not create executable rows.
                if(row.Parameter==""&&row.Reference==""&&row.Range==""&&row.Keyword=="")continue;
                definition.Rows.Add(row);
                if(definition.Rows.Count>5000)throw new ArgumentException("工作表“"+name+"”超过5000条判读步骤");
            }
            return definition;
        }

        private static string FlightCell(Dictionary<int,string> cells,int column)
        {
            string text;
            return cells.TryGetValue(column,out text)?text:"";
        }

        private static bool IsFlightHeader(Dictionary<int,string> cells)
        {
            return Key(FlightCell(cells,1))=="飞行阶段" && Key(FlightCell(cells,2))=="飞行动作" &&
                new[]{"参数代号","判读参数代号"}.Contains(Key(FlightCell(cells,3))) &&
                Key(FlightCell(cells,4))=="参考值" && Key(FlightCell(cells,5))=="范围值" && Key(FlightCell(cells,7))=="关键字";
        }

        private static string ReadFlightCell(XElement cell,List<string> shared,int column)
        {
            var formula=cell.Element(S+"f");
            if(formula!=null&&(column==3||column==4||column==5||column==7))return "="+formula.Value;
            string text;
            try{text=CellValue(cell,shared).Trim();}
            catch(ArgumentException){return "#单元格无法解析";}
            string type=(string)cell.Attribute("t");
            if((column==4||column==5)&&(type==null||type=="n"))
            {
                double number;
                // Excel displays at most 15 significant digits for numeric cells. Text inputs retain their precision.
                if(Double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out number)&&
                    !Double.IsInfinity(number)&&!Double.IsNaN(number))
                    return number.ToString("G15",CultureInfo.InvariantCulture);
            }
            return text;
        }

        private static List<FlightMerge>[] FlightMerges(XDocument document,
            SortedDictionary<int,Dictionary<int,string>> cellsByRow,int headerRow)
        {
            var columns=new[]{new List<FlightMerge>(),new List<FlightMerge>()};
            foreach(var cell in document.Root.Elements(S+"mergeCells").Elements(S+"mergeCell"))
            {
                string address=(string)cell.Attribute("ref")??"";
                var match=Regex.Match(address,@"^([AB])([0-9]+):\1([0-9]+)$");
                int start,end;
                if(!match.Success||!Int32.TryParse(match.Groups[2].Value,out start)||
                    !Int32.TryParse(match.Groups[3].Value,out end)||start<=headerRow||end<=start)continue;
                int index=match.Groups[1].Value=="A"?0:1;
                Dictionary<int,string> cells;
                if(cellsByRow.TryGetValue(start,out cells))
                    columns[index].Add(new FlightMerge{Start=start,End=end,Label=FlightCell(cells,index+1)});
            }
            foreach(var column in columns)
            {
                column.Sort((a,b)=>a.Start.CompareTo(b.Start));
                for(int i=1;i<column.Count;i++)
                    if(column[i-1].End>=column[i].Start)throw new ArgumentException("飞行阶段或飞行动作列的合并范围重叠");
            }
            return columns;
        }

        private static string FlightLabel(int row,string own,List<FlightMerge> merges,ref int index)
        {
            while(index<merges.Count&&merges[index].End<row)index++;
            return index<merges.Count&&merges[index].Start<=row?merges[index].Label:own;
        }
    }
}
