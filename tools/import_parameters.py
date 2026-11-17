"""Legacy CLI wrapper for the native importer; keeps all import paths consistent."""
import argparse
import json
import subprocess
import tempfile
from pathlib import Path


def generate(source):
    executable = Path(__file__).resolve().parents[1] / "TelemetryMonitor.exe"
    with tempfile.TemporaryDirectory(prefix="telemetry-catalog-") as temporary:
        output = Path(temporary) / "decoder.json"
        result = subprocess.run([str(executable), "--generate-decoder", str(Path(source).resolve()), "--output", str(output)], capture_output=True)
        if result.returncode:
            raise ValueError((result.stderr or result.stdout).decode("utf-8", errors="replace"))
        return json.loads(output.read_text(encoding="utf-8"))["Catalog"]


def extract(source):
    catalog = generate(source)
    notes = [f"{issue['Sheet']}!{issue['Row']} {issue['Code']}: {issue['Message']}" for issue in catalog["Issues"]]
    return catalog["Parameters"], notes


def main():
    parser = argparse.ArgumentParser(description="Legacy parameters.json export; column M is ignored by the native importer.")
    parser.add_argument("workbook", type=Path)
    parser.add_argument("--output", type=Path, default=Path(__file__).resolve().parents[1] / "config" / "parameters.json")
    args = parser.parse_args()
    catalog = generate(args.workbook)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(catalog["Parameters"], ensure_ascii=False, indent=2), encoding="utf-8")
    report = {"source": catalog["SourceFile"], "parameters": len(catalog["Parameters"]),
              "supported": sum(p["Supported"] for p in catalog["Parameters"]),
              "groups": {s["Name"]: s["Parameters"] for s in catalog["Sheets"]},
              "assumptions": catalog["Assumptions"], "notes": catalog["Issues"]}
    args.output.with_name("import-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
