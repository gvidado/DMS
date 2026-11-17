"""Eleven-column mapping, ignored-cell isolation and seven-column round trip."""
import io,json,subprocess,sys,urllib.parse,urllib.request,xml.etree.ElementTree as ET,zipfile
from pathlib import Path
from integration_test import ROOT
from sequence_test import xlsx

S="{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
FIELDS=["Keyword","Description","Command","Condition","Mark","Expected","Timeout"]

def fixture():
    unused=["SHOULD_NOT_READ_E","SHOULD_NOT_READ_I","SHOULD_NOT_READ_J","SHOULD_NOT_READ_K"]
    def wide(key="",description="",command="",condition="",mark="",expected="",timeout="",ignored=None):
        e,i,j,k=ignored or unused
        return [key,description,command,condition,e,mark,expected,timeout,i,j,k]
    source=xlsx([
        ("新11列",[
            wide("","指令列优先","KUTC.YBC2133",mark="T0",expected="F:2",timeout="3"),
            wide("TEXT","说明中PSS001可补救","UNKNOWN",expected="T0:2",timeout="4"),
            wide("","纯参数判读",condition="TME.A0.value>0",expected="F:4",timeout="5"),
            wide(ignored=unused),
            wide("","忽略列的编号不参与识别","NOT_IN_LIBRARY",expected="F:5",timeout="6",
                 ignored=["BC2133","BC0001","YKE0021","PSS001"]),
            wide("PLUSE","人工继续后发令","K001",ignored=["BC0001","","",""]),
            wide(ignored=unused),
            wide("","保留原始行号的注入提示","YKM9999")
        ]),
        ("旧7列",[["TEXT","旧格式兼容","KUTC.YBC0001","","T0","F:1","2"]]),
        ("有效列错误",[
            wide("TEXT","超时公式需要提示",mark="T0",expected="F:2",timeout="3"),
            wide("TEXT","期望时间错误需要提示",expected="F:3",timeout="4")
        ]),
        ("简写表头",[wide("","单行规则","","TME.A0.value>0",mark="T0",expected="F:6",timeout="7")])
    ])
    output=io.BytesIO()
    with zipfile.ZipFile(io.BytesIO(source)) as original,zipfile.ZipFile(output,"w",zipfile.ZIP_DEFLATED) as result:
        for name in original.namelist():
            data=original.read(name)
            if name in ["xl/worksheets/sheet1.xml","xl/worksheets/sheet3.xml","xl/worksheets/sheet4.xml"]:
                sheet=ET.fromstring(data);header=sheet.find(S+"sheetData").find(S+"row")
                header.clear();header.set("r","1")
                headings=["关键字","测试工作程序内容及要求","指令或参数","判读要求","不读取E","标志","期望执行时间","超时时间","不读取I","不读取J","不读取K"]
                if name.endswith("sheet4.xml"):
                    headings[1]="测试工作内容";headings[3]="判读";headings[7]="超时"
                for col,text in enumerate(headings):
                    cell=ET.SubElement(header,S+"c",r=chr(65+col)+"1",t="inlineStr")
                    ET.SubElement(ET.SubElement(cell,S+"is"),S+"t").text=text
                def alter(address,kind,text):
                    cell=sheet.find(".//"+S+"c[@r='"+address+"']");assert cell is not None,address
                    cell.clear();cell.set("r",address)
                    if kind=="formula":ET.SubElement(cell,S+"f").text=text
                    else:cell.set("t",kind);ET.SubElement(cell,S+"v").text=text
                if name.endswith("sheet1.xml"):
                    alter("E1","s","NOT_A_SHARED_STRING_NUMBER")
                    alter("E3","formula","1/0");alter("I3","e","#DIV/0!");alter("J3","s","999999")
                    alter("E8","formula","1/0");alter("I8","e","#VALUE!");alter("J8","s","999999")
                if name.endswith("sheet3.xml"):
                    alter("H2","formula","2+1");alter("G3","e","#VALUE!")
                data=ET.tostring(sheet,encoding="utf-8")
            result.writestr(name,data)
    return output.getvalue()

def run_checks(client,root,output,check,password,browser=False):
    def upload(data,convert=True):
        query=urllib.parse.urlencode(dict(filename="11列新序列表.xlsx",mode="execute",timeout=3,sourceIP="127.0.0.1",convert="commands" if convert else ""))
        request=urllib.request.Request(client.base+"/api/sequence/preview?"+query,data=data,
            headers={"Content-Type":"application/octet-stream","X-CSRF-Token":client.csrf})
        return json.loads(client.opener.open(request,timeout=20).read())
    data=fixture();input_file=root/"11列新序列表.xlsx";input_file.write_bytes(data)
    old_library=(root/"config/observed-commands.json").read_bytes()
    plain=upload(data,False)
    check(len(plain["definitions"])==4,"one workbook reads mixed seven/eleven-column sheets and short header labels")
    first=plain["definitions"][0]["definition"]["Rows"];by_row={row["Row"]:row for row in first}
    check(by_row[2]["Mark"]=="T0" and by_row[2]["Expected"]=="F:2" and by_row[2]["Timeout"]=="3",
          "eleven-column F/G/H map to mark, expected time and timeout")
    check([row["Row"] for row in first]==[2,3,4,6,7,9],
          "ignored-only rows disappear while all retained rows keep their original Excel row numbers")
    check(by_row[2]["Command"]=="KUTC.YBC2133",
          "ordinary preview maps eleven columns without silently converting command syntax")
    check(all(not row["CellErrors"] for row in first),
          "formulas, Excel error values and broken shared-string indexes in ignored columns do not cause errors")
    errors=plain["definitions"][2]["definition"]["Rows"]
    check("Timeout" in errors[0]["CellErrors"] and "第8列" in errors[0]["CellErrors"]["Timeout"]
          and "Expected" in errors[1]["CellErrors"] and "第7列" in errors[1]["CellErrors"]["Expected"],
          "errors in used H/G columns identify the correct logical field and original source column")
    converted=upload(data);rows=converted["definitions"][0]["definition"]["Rows"];by_row={row["Row"]:row for row in rows}
    check(by_row[2]["Command"]=="BC2133" and by_row[3]["Command"]=="PSS001" and by_row[3]["Keyword"]=="",
          "new eleven-column sheets retain command priority and description fallback rules")
    check(by_row[6]["Command"]=="NOT_IN_LIBRARY" and by_row[6]["Keyword"]=="TEXT",
          "library codes in E/I/J/K cannot rescue an unknown command")
    check(by_row[4]["Condition"]=="TME.A0.value>0" and by_row[4]["Keyword"]==""
          and by_row[7]["Keyword"]=="PLUSE" and by_row[7]["Command"]=="K001",
          "D parameter judgment and PLUSE commands preserve their current meanings")
    check(any(record["row"]==9 and "第9行YKE/YKM指令未入库" in record["reason"] for record in converted["conversion"]),
          "missing injection diagnostics use original row numbers even after ignored rows are removed")
    old=converted["definitions"][1]["definition"]["Rows"][0]
    check(old["Command"]=="BC0001" and (old["Mark"],old["Expected"],old["Timeout"])==("T0","F:1","2"),
          "legacy seven-column timing and command conversion remain unchanged")
    request=urllib.request.Request(client.base+"/api/sequence/export-converted",
        data=json.dumps(dict(token=converted["token"])).encode(),
        headers={"Content-Type":"application/json","X-CSRF-Token":client.csrf})
    response=client.opener.open(request,timeout=20);export=response.read()
    (output/"sequence-eleven-converted.xlsx").write_bytes(export)
    check(urllib.parse.unquote(response.headers["Content-Disposition"]).find("11列新序列表_判读格式匹配.xlsx")>=0,
          "download filename retains the input name plus the established format-matching suffix")
    with zipfile.ZipFile(io.BytesIO(export)) as workbook:
        worksheets=[name for name in workbook.namelist() if name.startswith("xl/worksheets/") and name.endswith(".xml")]
        check(len(worksheets)==4 and workbook.testzip() is None,"conversion exports every sheet as a valid XLSX archive")
        for name in worksheets:
            sheet=ET.fromstring(workbook.read(name))
            assert all(cell.get("r")[0] in "ABCDEFG" for cell in sheet.iter(S+"c")),name
            assert len(sheet.find(S+"sheetData").find(S+"row"))==7,name
            assert not list(sheet.iter(S+"f")),name
            assert b"SHOULD_NOT_READ" not in workbook.read(name),name
        check(True,"all output sheets contain exactly seven columns A-G and no ignored data or executable formulas")
        sheet=ET.fromstring(workbook.read("xl/worksheets/sheet1.xml"))
        values={cell.get("r"):"".join(node.text or "" for node in cell.iter(S+"t")) for cell in sheet.iter(S+"c")}
        check((values["E2"],values["F2"],values["G2"])==("T0","F:2","3"),
              "export places source F/G/H into standard output E/F/G")
    again=upload(export,False)
    check([[{key:row[key] for key in ["Row"]+FIELDS} for row in item["definition"]["Rows"]] for item in again["definitions"]]
          ==[[{key:row[key] for key in ["Row"]+FIELDS} for row in item["definition"]["Rows"]] for item in converted["definitions"]],
          "the seven-column export reimports with identical step text, timing and original row numbers")
    if browser:
        node_root=Path(sys.executable).parent.parent/"node"
        run=subprocess.run([str(node_root/"bin/node.exe"),str(ROOT/"tools/sequence_eleven_columns_browser.mjs"),str(node_root)],
            input=json.dumps(dict(base=client.base,password=password,file=str(input_file),output=str(output))),
            capture_output=True,text=True,encoding="utf-8",timeout=100,creationflags=subprocess.CREATE_NO_WINDOW)
        print(run.stdout,flush=True);assert run.returncode==0,run.stderr
    check((root/"config/observed-commands.json").read_bytes()==old_library
          and client.call("/api/sequence/config")["definitions"]==[],
          "eleven-column preview and export never alter the command library or save/run sequences")
