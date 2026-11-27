using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace TelemetryMonitor
{
    // Streaming XLSX: each worksheet is completed before opening another ZIP entry.
    internal sealed class HistoryWorkbookWriter : IDisposable
    {
        internal const int ParametersPerSheet=16383;
        private const int WorksheetRows=1048576;
        private const string Spreadsheet="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private readonly ZipArchive zip;
        private readonly List<string> sheets=new List<string>();
        private HistoryWideColumn[] columns;
        private string[] addresses;
        private Stream sheetStream;
        private XmlWriter sheet;
        private int row;

        internal HistoryWorkbookWriter(Stream output)
        {zip=new ZipArchive(new PositionedOutput(output),ZipArchiveMode.Create,true);}

        // Framework ZIP writers inspect Position even for a forward-only HTTP stream.
        private sealed class PositionedOutput : Stream
        {
            private readonly Stream destination;
            private long position;
            internal PositionedOutput(Stream destination){this.destination=destination;}
            public override bool CanRead{get{return false;}}
            public override bool CanSeek{get{return false;}}
            public override bool CanWrite{get{return true;}}
            public override long Length{get{return position;}}
            public override long Position{get{return position;}set{throw new NotSupportedException();}}
            public override void Write(byte[] buffer,int offset,int count){destination.Write(buffer,offset,count);position+=count;}
            public override void Flush(){destination.Flush();}
            public override int Read(byte[] buffer,int offset,int count){throw new NotSupportedException();}
            public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
            public override void SetLength(long value){throw new NotSupportedException();}
        }

        internal void StartColumns(HistoryWideColumn[] selected)
        {
            FinishSheet();
            columns=selected;
            addresses=new string[columns.Length+1];
            for(int i=0;i<addresses.Length;i++)addresses[i]=ColumnAddress(i+1);
            StartSheet();
        }
        private void StartSheet()
        {
            sheets.Add("参数历史"+(sheets.Count+1));
            sheetStream=zip.CreateEntry("xl/worksheets/sheet"+sheets.Count+".xml",CompressionLevel.Fastest).Open();
            sheet=XmlWriter.Create(sheetStream,new XmlWriterSettings{Encoding=new UTF8Encoding(false),CloseOutput=false});
            sheet.WriteStartDocument();
            sheet.WriteStartElement("worksheet",Spreadsheet);
            sheet.WriteStartElement("sheetViews",Spreadsheet);
            sheet.WriteStartElement("sheetView",Spreadsheet);sheet.WriteAttributeString("workbookViewId","0");
            sheet.WriteStartElement("pane",Spreadsheet);sheet.WriteAttributeString("ySplit","1");
            sheet.WriteAttributeString("topLeftCell","A2");sheet.WriteAttributeString("state","frozen");sheet.WriteEndElement();
            sheet.WriteEndElement();sheet.WriteEndElement();
            sheet.WriteStartElement("sheetData",Spreadsheet);
            row=1;sheet.WriteStartElement("row",Spreadsheet);sheet.WriteAttributeString("r","1");
            Cell(0,"时间",false);
            for(int i=0;i<columns.Length;i++)
            {
                var column=columns[i];
                string label=column.label;
                if(!String.IsNullOrEmpty(column.unit))label+=" ("+column.unit+")";
                Cell(i+1,label,false);
            }
            sheet.WriteEndElement();
        }
        internal void Add(Dictionary<string,object> record)
        {
            if(row>=WorksheetRows){FinishSheet();StartSheet();}
            row++;
            sheet.WriteStartElement("row",Spreadsheet);sheet.WriteAttributeString("r",row.ToString(CultureInfo.InvariantCulture));
            Cell(0,Convert.ToString(record["time"],CultureInfo.InvariantCulture).Replace('T',' '),false);
            var values=record["values"] as Dictionary<string,object>;
            for(int i=0;i<columns.Length;i++)
            {
                object input;
                if(values==null||!values.TryGetValue(columns[i].key,out input))continue;
                var value=input as Dictionary<string,object>;
                object text,kind;
                if(value==null||!value.TryGetValue("value",out text)||text==null)continue;
                value.TryGetValue("valueKind",out kind);
                string literal=Convert.ToString(text,CultureInfo.InvariantCulture);
                double number;
                bool numeric=(Convert.ToString(kind)=="number"||Convert.ToString(kind)=="integer")&&literal.Length<=15&&
                    Double.TryParse(literal,NumberStyles.Float,CultureInfo.InvariantCulture,out number)&&!Double.IsNaN(number)&&!Double.IsInfinity(number);
                Cell(i+1,literal,numeric);
            }
            sheet.WriteEndElement();
        }
        private void Cell(int column,string value,bool numeric)
        {
            if(value.Length>32767)throw new InvalidDataException("参数文本超过Excel单元格32767字符限制，请使用数据底座JSONL导出");
            sheet.WriteStartElement("c",Spreadsheet);sheet.WriteAttributeString("r",addresses[column]+row);
            sheet.WriteAttributeString("t",numeric?"n":"inlineStr");
            if(numeric)sheet.WriteElementString("v",Spreadsheet,value);
            else
            {
                sheet.WriteStartElement("is",Spreadsheet);sheet.WriteStartElement("t",Spreadsheet);
                sheet.WriteAttributeString("xml","space","http://www.w3.org/XML/1998/namespace","preserve");
                sheet.WriteString(value);sheet.WriteEndElement();sheet.WriteEndElement();
            }
            sheet.WriteEndElement();
        }
        private static string ColumnAddress(int index)
        {
            string value="";
            while(index>0){index--;value=(char)('A'+index%26)+value;index/=26;}
            return value;
        }
        private void FinishSheet()
        {
            if(sheet==null)return;
            sheet.WriteEndElement();sheet.WriteEndElement();sheet.WriteEndDocument();
            sheet.Dispose();sheet=null;sheetStream.Dispose();sheetStream=null;
        }
        private void WriteXml(string path,Action<XmlWriter> write)
        {
            using(var stream=zip.CreateEntry(path).Open())
            using(var xml=XmlWriter.Create(stream,new XmlWriterSettings{Encoding=new UTF8Encoding(false)}))
            {xml.WriteStartDocument();write(xml);xml.WriteEndDocument();}
        }
        public void Dispose()
        {
            FinishSheet();
            WriteXml("[Content_Types].xml",xml=>{
                const string ns="http://schemas.openxmlformats.org/package/2006/content-types";
                xml.WriteStartElement("Types",ns);
                foreach(var item in new[]{new[]{"rels","application/vnd.openxmlformats-package.relationships+xml"},new[]{"xml","application/xml"}})
                {xml.WriteStartElement("Default",ns);xml.WriteAttributeString("Extension",item[0]);xml.WriteAttributeString("ContentType",item[1]);xml.WriteEndElement();}
                for(int i=0;i<=sheets.Count;i++)
                {xml.WriteStartElement("Override",ns);xml.WriteAttributeString("PartName",i==0?"/xl/workbook.xml":"/xl/worksheets/sheet"+i+".xml");xml.WriteAttributeString("ContentType",i==0?"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml":"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");xml.WriteEndElement();}
                xml.WriteEndElement();
            });
            WriteXml("xl/workbook.xml",xml=>{
                xml.WriteStartElement("workbook",Spreadsheet);xml.WriteStartElement("sheets",Spreadsheet);
                for(int i=0;i<sheets.Count;i++)
                {xml.WriteStartElement("sheet",Spreadsheet);xml.WriteAttributeString("name",sheets[i]);xml.WriteAttributeString("sheetId",(i+1).ToString());xml.WriteAttributeString("r","id","http://schemas.openxmlformats.org/officeDocument/2006/relationships","rId"+(i+1));xml.WriteEndElement();}
                xml.WriteEndElement();xml.WriteEndElement();
            });
            WriteXml("xl/_rels/workbook.xml.rels",xml=>{
                const string ns="http://schemas.openxmlformats.org/package/2006/relationships";
                xml.WriteStartElement("Relationships",ns);
                for(int i=0;i<sheets.Count;i++)
                {xml.WriteStartElement("Relationship",ns);xml.WriteAttributeString("Id","rId"+(i+1));xml.WriteAttributeString("Type","http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet");xml.WriteAttributeString("Target","worksheets/sheet"+(i+1)+".xml");xml.WriteEndElement();}
                xml.WriteEndElement();
            });
            WriteXml("_rels/.rels",xml=>{
                const string ns="http://schemas.openxmlformats.org/package/2006/relationships";
                xml.WriteStartElement("Relationships",ns);xml.WriteStartElement("Relationship",ns);
                xml.WriteAttributeString("Id","rId1");xml.WriteAttributeString("Type","http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument");
                xml.WriteAttributeString("Target","xl/workbook.xml");xml.WriteEndElement();xml.WriteEndElement();
            });
            zip.Dispose();
        }
    }
}
