import argparse
import json
import random
from pathlib import Path
from typing import Any

from PIL import Image


ROI_KEYS = [
    "name",
    "level",
    "mainStat",
    "mainStatValue",
    "subStat1",
    "subStatValue1",
    "subStat2",
    "subStatValue2",
    "subStat3",
    "subStatValue3",
    "subStat4",
    "subStatValue4",
]


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def parse_number(value: Any) -> tuple[float | None, bool]:
    if isinstance(value, (int, float)):
        return float(value), False
    text = str(value).strip()
    if text.endswith("%"):
        try:
            return float(text[:-1]), True
        except ValueError:
            return None, True
    try:
        return float(text), False
    except ValueError:
        return None, False


def label_value(value: Any) -> str:
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return str(value)


def first_key_value(obj: dict[str, Any]) -> tuple[str, Any]:
    if not obj:
        return "", ""
    key = next(iter(obj))
    return key, obj[key]


def load_substat_bases(stat_rules: dict[str, Any], rarity: str) -> dict[str, float]:
    values = stat_rules.get("subStatValues", {}).get(rarity, {})
    bases: dict[str, float] = {}
    for name, triple in values.items():
        if not triple:
            continue
        base, _ = parse_number(triple[0])
        if base is not None:
            bases[name] = base
    return bases


def substat_label(name: str, value: Any, bases: dict[str, float]) -> tuple[str, bool]:
    numeric, is_percent = parse_number(value)
    key = f"{name}%" if is_percent and not name.endswith("%") else name
    base = bases.get(key)
    needs_review = False
    suffix = 0
    if numeric is not None and base:
        ratio = numeric / base
        rounded = int(round(ratio))
        if rounded >= 1 and abs(ratio - rounded) <= 0.18:
            suffix = rounded - 1
        else:
            needs_review = True
    else:
        needs_review = True

    if suffix > 0:
        return f"{name} +{suffix}", needs_review
    return name, needs_review


def build_labels(item: dict[str, Any], roi_count: int, stat_rules: dict[str, Any]) -> tuple[list[str], list[str]]:
    rarity = str(item.get("品质", "S"))
    level = int(item.get("等级", 0) or 0)
    max_level = int(item.get("最大等级", 15) or 15)
    slot = int(item.get("槽位", 0) or 0)
    bases = load_substat_bases(stat_rules, rarity)
    labels: list[str] = []
    issues: list[str] = []

    main_name, main_value = first_key_value(item.get("主属性", {}))
    substats = item.get("副属性", []) or []

    for field in ROI_KEYS[:roi_count]:
        if field == "name":
            labels.append(f"{item.get('名称', '')}【{slot}】")
            if slot < 1 or slot > 6:
                issues.append("slot_out_of_range")
            continue

        if field == "level":
            level_text = f"{level:02d}" if level < 10 else str(level)
            labels.append(f"等级{level_text}/{max_level}")
            if level < 0 or level > max_level:
                issues.append("level_out_of_range")
            continue

        if field == "mainStat":
            labels.append(main_name)
            if not main_name:
                issues.append("missing_main_stat")
            continue

        if field == "mainStatValue":
            labels.append(label_value(main_value))
            if main_value == "":
                issues.append("missing_main_stat_value")
            continue

        if field.startswith("subStatValue"):
            index = int(field.removeprefix("subStatValue")) - 1
            if index >= len(substats):
                labels.append("")
                issues.append(f"missing_{field}")
                continue
            _, value = first_key_value(substats[index])
            labels.append(label_value(value))
            continue

        if field.startswith("subStat"):
            index = int(field.removeprefix("subStat")) - 1
            if index >= len(substats):
                labels.append("")
                issues.append(f"missing_{field}")
                continue
            name, value = first_key_value(substats[index])
            label, review = substat_label(name, value, bases)
            labels.append(label)
            if review:
                issues.append(f"weak_roll_suffix_{field}")
            continue

        labels.append("")
        issues.append(f"unknown_field_{field}")

    return labels, sorted(set(issues))


def split_indices(items: list[dict[str, Any]], seed: int) -> dict[int, str]:
    by_group: dict[str, list[int]] = {"level15": [], "mixed": []}
    for item in items:
        group = "level15" if int(item.get("等级", 0) or 0) == 15 else "mixed"
        by_group[group].append(int(item["序号"]))

    rng = random.Random(seed)
    split: dict[int, str] = {}
    for indices in by_group.values():
        rng.shuffle(indices)
        n = len(indices)
        test_n = max(1, round(n * 0.10)) if n >= 10 else 0
        val_n = max(1, round(n * 0.10)) if n >= 10 else 0
        test = set(indices[:test_n])
        val = set(indices[test_n:test_n + val_n])
        for index in indices:
            split[index] = "test" if index in test else "val" if index in val else "train"
    return split


def prepare(scan_dir: Path, out_dir: Path, stat_rules_file: Path, seed: int) -> dict[str, Any]:
    samples_dir = scan_dir / "ocr-samples"
    export_items = read_json(scan_dir / "export.json")
    stat_rules = read_json(stat_rules_file)
    export_by_index = {int(item["序号"]): item for item in export_items}
    split_by_index = split_indices(export_items, seed)

    crops_dir = out_dir / "crops"
    manifest_path = out_dir / "manifest.jsonl"
    char_set: set[str] = set()
    rows: list[dict[str, Any]] = []
    item_issues: dict[int, list[str]] = {}

    for metadata_file in sorted(samples_dir.glob("*.json")):
        metadata = read_json(metadata_file)
        index = int(metadata["Index"])
        panel_file = metadata_file.with_suffix(".png")
        item = export_by_index.get(index)
        if item is None or not panel_file.exists():
            continue

        rois = metadata.get("Rois", [])
        labels, issues = build_labels(item, len(rois), stat_rules)
        item_issues[index] = issues
        split = split_by_index.get(index, "train")
        is_level15 = int(item.get("等级", 0) or 0) == 15
        panel = Image.open(panel_file).convert("RGB")

        for roi_index, roi in enumerate(rois):
            if roi_index >= len(labels):
                continue
            label = labels[roi_index]
            field = ROI_KEYS[roi_index]
            if not label:
                continue
            x, y, w, h = int(roi["X"]), int(roi["Y"]), int(roi["Width"]), int(roi["Height"])
            crop = panel.crop((x, y, x + w, y + h))
            crop_rel = Path("crops") / split / f"{index:05d}_{roi_index:02d}_{field}.png"
            crop_path = out_dir / crop_rel
            crop_path.parent.mkdir(parents=True, exist_ok=True)
            crop.save(crop_path)
            char_set.update(label)
            rows.append({
                "scan": scan_dir.name,
                "index": index,
                "split": split,
                "field": field,
                "roiIndex": roi_index,
                "image": crop_rel.as_posix(),
                "label": label,
                "rarity": item.get("品质"),
                "level": item.get("等级"),
                "maxLevel": item.get("最大等级"),
                "slot": item.get("槽位"),
                "isLevel15": is_level15,
                "visibleRoiCount": len(rois),
                "needsReview": bool(issues),
                "issues": issues,
            })

    out_dir.mkdir(parents=True, exist_ok=True)
    with manifest_path.open("w", encoding="utf-8", newline="\n") as handle:
        for row in rows:
            handle.write(json.dumps(row, ensure_ascii=False) + "\n")

    vocab = ["<blank>"] + sorted(char_set)
    (out_dir / "charset.txt").write_text("\n".join(vocab) + "\n", encoding="utf-8")
    summary = {
        "scanDir": str(scan_dir),
        "outputDir": str(out_dir),
        "items": len(export_items),
        "manifestRows": len(rows),
        "charsetSizeWithBlank": len(vocab),
        "splits": {},
        "level15Items": sum(1 for item in export_items if int(item.get("等级", 0) or 0) == 15),
        "non15Items": sum(1 for item in export_items if int(item.get("等级", 0) or 0) != 15),
        "needsReviewItems": sum(1 for issues in item_issues.values() if issues),
    }
    for row in rows:
        summary["splits"].setdefault(row["split"], 0)
        summary["splits"][row["split"]] += 1
    write_json(out_dir / "summary.json", summary)
    return summary


def main() -> int:
    parser = argparse.ArgumentParser(description="Prepare cropped OCR ROI dataset from a scan directory.")
    parser.add_argument("--scan-dir", required=True, type=Path)
    parser.add_argument("--out-dir", required=True, type=Path)
    parser.add_argument("--stat-rules", default=Path("Data/stat_rules.json"), type=Path)
    parser.add_argument("--seed", default=20260624, type=int)
    args = parser.parse_args()

    summary = prepare(args.scan_dir, args.out_dir, args.stat_rules, args.seed)
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
