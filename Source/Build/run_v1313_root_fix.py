from pathlib import Path

patcher = Path("Source/Build/apply_v1313_root_fix.py")
text = patcher.read_text(encoding="utf-8")
old = "replace_exact(Path(\".github/workflows/build.yml\"), 'ExtremeRagdoll-v1.3.12.zip', 'ExtremeRagdoll-v1.3.13.zip')"
new = '''build_workflow = Path(".github/workflows/build.yml")
build_text, build_newline, build_bom = read_preserving(build_workflow)
if build_text.count("ExtremeRagdoll-v1.3.12.zip") != 2:
    raise RuntimeError("build workflow artifact ZIP occurrence count changed")
write_preserving(
    build_workflow,
    build_text.replace("ExtremeRagdoll-v1.3.12.zip", "ExtremeRagdoll-v1.3.13.zip"),
    build_newline,
    build_bom)'''
if text.count(old) != 1:
    raise RuntimeError("v1.3.13 patcher artifact-name statement changed")
text = text.replace(old, new)
namespace = {"__name__": "__main__", "__file__": str(patcher)}
exec(compile(text, str(patcher), "exec"), namespace)
