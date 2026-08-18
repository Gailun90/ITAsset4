import os, zipfile, hashlib, json

ROOT = r"C:\Users\wzyou\Documents\WPF2\ITAsset4"
BINS = {
    "Service": os.path.join(ROOT, "ITAsset4.Service", "bin", "Release", "net48"),
    "Tray":    os.path.join(ROOT, "ITAsset4.Tray", "bin", "Release", "net48"),
    "Updater": os.path.join(ROOT, "ITAsset4.Updater", "bin", "Release", "net48"),
}
VERSION = "1.2.18"
OUT_DIR = os.path.join(ROOT, "publish", VERSION)
os.makedirs(OUT_DIR, exist_ok=True)
ZIP_PATH = os.path.join(OUT_DIR, "update.zip")

# Flatten the union of all three outputs; skip .pdb to keep the package lean.
seen = {}
for proj, d in BINS.items():
    for fn in os.listdir(d):
        if fn.lower().endswith(".pdb"):
            continue
        full = os.path.join(d, fn)
        if not os.path.isfile(full):
            continue
        seen.setdefault(fn, full)  # first occurrence wins; dup deps are identical

with zipfile.ZipFile(ZIP_PATH, "w", zipfile.ZIP_DEFLATED) as z:
    for fn, full in seen.items():
        z.write(full, fn)

sha = hashlib.sha256()
with open(ZIP_PATH, "rb") as f:
    for chunk in iter(lambda: f.read(1 << 20), b""):
        sha.update(chunk)
sha_hex = sha.hexdigest()
size = os.path.getsize(ZIP_PATH)

# Server-side metadata consumed by GET /api/client/update (ClientUpdateInfo schema).
# NOTE: replace <SERVER> with the real download host before deploying.
version_json = {
    "available": True,
    "version": VERSION,
    "url": f"https://<SERVER>/downloads/client/itasset4-update-{VERSION}.zip",
    "hash": sha_hex,
    "size": size,
    "mandatory": True,
    "notes": (
        "SessionManager 改为在公共 Startup 文件夹创建快捷方式拉起 Tray，"
        "移除 CreateProcessAsUser 与计划任务拉起逻辑；依赖 WTS 仅保留会话解析。"
        "版本 1.2.18。"
    ),
}
with open(os.path.join(OUT_DIR, "version.json"), "w", encoding="utf-8") as f:
    json.dump(version_json, f, ensure_ascii=False, indent=2)

print("Files in update.zip:", len(seen))
for fn in sorted(seen):
    print("   ", fn)
print("zip     :", ZIP_PATH)
print("sha256  :", sha_hex)
print("size    :", size, "bytes")
print("version.json written to", os.path.join(OUT_DIR, "version.json"))
