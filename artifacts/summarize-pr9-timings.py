import json, re, statistics
from pathlib import Path
rows = []
for line in Path("artifacts/pr9-three-long-progress.log").read_text(encoding="utf-8-sig").splitlines():
    if not line.startswith("paid-service scenario="): continue
    row = {}
    for field in line.removeprefix("paid-service ").split("; "):
        key, value = field.split("=", 1)
        row[key] = int(value.removesuffix("ms")) if re.fullmatch(r"[0-9]+(?:ms)?", value) else value
    rows.append(row)
pairs=[]
for name in sorted({r["scenario"] for r in rows}):
    by_mode={r["mode"]:r for r in rows if r["scenario"]==name}
    if len(by_mode)!=2: continue
    c,i=by_mode["Complete"],by_mode["Incremental"]
    saved=c["first-audio-consumption"]-i["first-audio-consumption"]
    pairs.append({"scenario":name,"complete":c,"incremental":i,"first_audio_saved_ms":saved,
       "first_audio_saved_percent":saved/c["first-audio-consumption"]*100,
       "total_extra_ms":i["audio-ended"]-c["audio-ended"],
       "after_sentence_complete_ms":c["first-audio-consumption"]-c["first-readable-sentence"],
       "after_sentence_incremental_ms":i["first-audio-consumption"]-i["first-readable-sentence"]})
result={"runs":rows,"pairs":pairs}
if pairs:
    result["aggregate"]={"pair_count":len(pairs),"faster_pair_count":sum(p["first_audio_saved_ms"]>0 for p in pairs),
      "median_saved_ms":statistics.median(p["first_audio_saved_ms"] for p in pairs),
      "mean_saved_ms":statistics.mean(p["first_audio_saved_ms"] for p in pairs),
      "median_paired_saved_percent":statistics.median(p["first_audio_saved_percent"] for p in pairs)}
print(json.dumps(result,ensure_ascii=False,indent=2))
