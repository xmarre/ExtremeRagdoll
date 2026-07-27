from pathlib import Path

patcher = Path("Source/Build/apply_v1313_root_fix.py")
text = patcher.read_text(encoding="utf-8")
old_zip = "replace_exact(Path(\".github/workflows/build.yml\"), 'ExtremeRagdoll-v1.3.12.zip', 'ExtremeRagdoll-v1.3.13.zip')"
old_name = "replace_exact(Path(\".github/workflows/build.yml\"), 'name: ExtremeRagdoll-v1.3.12', 'name: ExtremeRagdoll-v1.3.13')"
new_zip = '''build_workflow = Path(".github/workflows/build.yml")
build_text, _, _ = read_preserving(build_workflow)
if build_text.count("ExtremeRagdoll-v1.3.13.zip") != 2:
    raise RuntimeError("build workflow is not pre-versioned to v1.3.13")'''
new_name = '''if build_text.count("name: ExtremeRagdoll-v1.3.13") != 1:
    raise RuntimeError("build workflow artifact name is not v1.3.13")'''
if text.count(old_zip) != 1 or text.count(old_name) != 1:
    raise RuntimeError("v1.3.13 patcher workflow statements changed")
text = text.replace(old_zip, new_zip).replace(old_name, new_name)
namespace = {"__name__": "__main__", "__file__": str(patcher)}
exec(compile(text, str(patcher), "exec"), namespace)
