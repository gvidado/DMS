"""Generate local, synthetic Excel inputs for telemetry_import_review_browser.mjs."""
from pathlib import Path
from openpyxl import Workbook

out = Path(__file__).resolve().parents[1] / "test-output" / "telemetry-import-review"
out.mkdir(parents=True, exist_ok=True)
headers = ["序号", "备用", "参数代号", "参数名称", "起始字节", "字节个数", "起始位", "位置", "数据类型", "单位", "缺省显示类型", "精度", "公式(忽略)", "系数", "物理意义"]
w = Workbook(); w.active.title = "说明"; sheet = w.create_sheet("测试遥测"); sheet.append(headers)
for i in range(1, 121):
    code, name, start, width, bits, kind, scale = f"A{i}", f"测试参数{i}", (i - 1) * 2, 2, "", "无符号整数", 1
    position = f"Byte{start}~Byte{start + 1}"; precision = 2
    if i == 1: bits = 0
    if i == 2: start = 999
    if i == 3: precision = "/"
    if i == 4: precision = 15
    if i == 5: code = ""
    if i == 8: scale = "错误系数"
    if i == 11: code = "A10"
    if i == 12: name = ""
    if i == 14: width = 0
    if i == 16: kind = "未知类型"
    if i == 18: position = "Byte999999999999999999999~Byte999999999999999999999"
    if i == 20: bits, width, position = "B0", .5, f"Byte{start}_B0~Byte{start}_B3"
    if i == 22: code, name = "", ""
    sheet.append([i, "", code, name, start, width, bits, position, kind, "V", "十进制", precision, "不得读取M列公式", scale, "0:关闭;1:打开"])
sheet.append([121, "", "R", "预留", 240, 2, "", "Byte240~Byte241", "无符号整数", "", "", 2, "", 1, ""])
sheet = w.create_sheet("另一张表"); sheet.append(headers); sheet.append([1, "", "B1", "另一表首项", 0, 2, "", "Byte0~Byte1", "无符号整数", "", "", 2, "", 1, ""])
w.save(out / "错误遥测表.xlsx")
w = Workbook(); w.active.title = "说明"; sheet = w.create_sheet("测试遥测"); sheet.append(headers); sheet.append([1, "", "A1", "单表更新", 0, 2, "", "Byte0~Byte1", "无符号整数", "", "", 2, "", 2, ""]); w.save(out / "同名表.xlsx")
w = Workbook(); w.active.title = "说明"; w.create_sheet("无参数表头").append(["无效", "表格"]); w.save(out / "格式错误.xlsx")
print("Telemetry import fixtures generated.")
