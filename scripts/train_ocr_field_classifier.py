import argparse
import json
import math
import random
from collections import Counter
from pathlib import Path
from typing import Any

import torch
from PIL import Image
from torch import nn
from torch.utils.data import DataLoader, Dataset
from torchvision import transforms


def load_rows(root: Path, include_review: bool, fields: set[str] | None) -> list[dict[str, Any]]:
    rows = []
    with (root / "manifest.jsonl").open("r", encoding="utf-8") as handle:
        for line in handle:
            row = json.loads(line)
            if row.get("needsReview") and not include_review:
                continue
            if not row.get("label"):
                continue
            if fields and row.get("field") not in fields:
                continue
            rows.append(row)
    return rows


class FieldDataset(Dataset):
    def __init__(
        self,
        root: Path,
        rows: list[dict[str, Any]],
        split: str,
        class_to_id: dict[str, int],
        field_to_id: dict[str, int],
        image_height: int,
        max_samples: int,
        seed: int,
    ):
        self.root = root
        self.class_to_id = class_to_id
        self.field_to_id = field_to_id
        self.image_height = image_height
        self.rows = [row for row in rows if row["split"] == split]
        if max_samples > 0 and len(self.rows) > max_samples:
            rng = random.Random(seed)
            rng.shuffle(self.rows)
            self.rows = self.rows[:max_samples]
        self.transform = transforms.Compose([
            transforms.Grayscale(num_output_channels=1),
            transforms.ToTensor(),
            transforms.Normalize((0.5,), (0.5,)),
        ])

    def __len__(self) -> int:
        return len(self.rows)

    def __getitem__(self, idx: int) -> dict[str, Any]:
        row = self.rows[idx]
        image = Image.open(self.root / row["image"]).convert("RGB")
        height = self.image_height
        width = max(8, int(round(image.width * height / image.height)))
        image = image.resize((width, height), Image.Resampling.BILINEAR)
        key = f"{row['field']}\t{row['label']}"
        return {
            "image": self.transform(image),
            "fieldId": torch.tensor(self.field_to_id[row["field"]], dtype=torch.long),
            "target": torch.tensor(self.class_to_id[key], dtype=torch.long),
            "classKey": key,
            "label": row["label"],
            "row": row,
        }


def collate(batch: list[dict[str, Any]]) -> dict[str, Any]:
    max_width = max(item["image"].shape[-1] for item in batch)
    images = []
    for item in batch:
        image = item["image"]
        pad = max_width - image.shape[-1]
        if pad > 0:
            image = torch.nn.functional.pad(image, (0, pad), value=-1.0)
        images.append(image)
    return {
        "images": torch.stack(images),
        "fieldIds": torch.stack([item["fieldId"] for item in batch]),
        "targets": torch.stack([item["target"] for item in batch]),
        "classKeys": [item["classKey"] for item in batch],
        "labels": [item["label"] for item in batch],
        "rows": [item["row"] for item in batch],
    }


def mask_logits(logits: torch.Tensor, field_ids: torch.Tensor, class_field_ids: torch.Tensor) -> torch.Tensor:
    allowed = class_field_ids.unsqueeze(0).eq(field_ids.unsqueeze(1))
    return logits.masked_fill(~allowed, -1.0e4)


class FieldAwareClassifier(nn.Module):
    def __init__(self, classes: int, fields: int, base_channels: int, field_embedding: int):
        super().__init__()
        c1 = base_channels
        c2 = base_channels * 2
        c3 = base_channels * 4
        c4 = base_channels * 6
        self.features = nn.Sequential(
            nn.Conv2d(1, c1, 3, padding=1),
            nn.BatchNorm2d(c1),
            nn.ReLU(inplace=True),
            nn.MaxPool2d(2, 2),
            nn.Conv2d(c1, c2, 3, padding=1),
            nn.BatchNorm2d(c2),
            nn.ReLU(inplace=True),
            nn.MaxPool2d(2, 2),
            nn.Conv2d(c2, c3, 3, padding=1),
            nn.BatchNorm2d(c3),
            nn.ReLU(inplace=True),
            nn.MaxPool2d((2, 1)),
            nn.Conv2d(c3, c4, 3, padding=1),
            nn.BatchNorm2d(c4),
            nn.ReLU(inplace=True),
            nn.AdaptiveAvgPool2d((1, 1)),
        )
        self.field_embedding = nn.Embedding(fields, field_embedding)
        self.classifier = nn.Sequential(
            nn.Linear(c4 + field_embedding, c4 * 2),
            nn.ReLU(inplace=True),
            nn.Dropout(0.15),
            nn.Linear(c4 * 2, classes),
        )

    def forward(self, images: torch.Tensor, field_ids: torch.Tensor) -> torch.Tensor:
        feats = self.features(images).flatten(1)
        fields = self.field_embedding(field_ids)
        return self.classifier(torch.cat([feats, fields], dim=1))


@torch.no_grad()
def evaluate(
    model: nn.Module,
    loader: DataLoader,
    id_to_class: list[str],
    class_field_ids: torch.Tensor,
    device: torch.device,
    limit_examples: int = 20,
) -> dict[str, Any]:
    model.eval()
    total = 0
    exact = 0
    by_field: dict[str, dict[str, int]] = {}
    examples = []
    for batch in loader:
        images = batch["images"].to(device)
        field_ids = batch["fieldIds"].to(device)
        logits = mask_logits(model(images, field_ids), field_ids, class_field_ids)
        predictions = logits.argmax(dim=1).cpu().tolist()
        for pred_id, target_id, row in zip(predictions, batch["targets"].tolist(), batch["rows"]):
            pred_key = id_to_class[pred_id]
            target_key = id_to_class[target_id]
            pred_field, pred_label = pred_key.split("\t", 1)
            total += 1
            ok = pred_key == target_key
            exact += int(ok)
            field = row["field"]
            stats = by_field.setdefault(field, {"total": 0, "exact": 0})
            stats["total"] += 1
            stats["exact"] += int(ok)
            if not ok and len(examples) < limit_examples:
                examples.append({
                    "field": field,
                    "label": row["label"],
                    "predictionField": pred_field,
                    "prediction": pred_label,
                    "image": row["image"],
                })
    return {
        "total": total,
        "exact": exact,
        "exactRate": exact / total if total else 0.0,
        "byField": {k: {"total": v["total"], "exactRate": v["exact"] / v["total"]} for k, v in sorted(by_field.items())},
        "examples": examples,
    }


def class_weights(train_set: FieldDataset, class_count: int) -> torch.Tensor:
    counts = Counter()
    for row in train_set.rows:
        counts[train_set.class_to_id[f"{row['field']}\t{row['label']}"]] += 1
    weights = torch.ones(class_count, dtype=torch.float32)
    for class_id, count in counts.items():
        weights[class_id] = 1.0 / math.sqrt(count)
    weights *= class_count / weights.sum()
    return weights


def main() -> int:
    parser = argparse.ArgumentParser(description="Train a field-aware OCR candidate classifier.")
    parser.add_argument("--dataset", required=True, type=Path)
    parser.add_argument("--out-dir", required=True, type=Path)
    parser.add_argument("--epochs", default=8, type=int)
    parser.add_argument("--batch-size", default=128, type=int)
    parser.add_argument("--lr", default=1e-3, type=float)
    parser.add_argument("--seed", default=20260624, type=int)
    parser.add_argument("--include-review", action="store_true")
    parser.add_argument("--height", default=32, type=int)
    parser.add_argument("--base-channels", default=12, type=int)
    parser.add_argument("--field-embedding", default=16, type=int)
    parser.add_argument("--max-train", default=0, type=int)
    parser.add_argument("--max-val", default=0, type=int)
    parser.add_argument("--max-test", default=0, type=int)
    parser.add_argument("--fields", default="", help="Comma-separated field names to train on.")
    parser.add_argument("--threads", default=0, type=int)
    parser.add_argument("--no-class-weights", action="store_true")
    args = parser.parse_args()

    random.seed(args.seed)
    torch.manual_seed(args.seed)
    if args.threads > 0:
        torch.set_num_threads(args.threads)

    fields = {field.strip() for field in args.fields.split(",") if field.strip()} or None
    rows = load_rows(args.dataset, include_review=args.include_review, fields=fields)
    class_keys = sorted({f"{row['field']}\t{row['label']}" for row in rows})
    field_names = sorted({row["field"] for row in rows})
    class_to_id = {key: index for index, key in enumerate(class_keys)}
    field_to_id = {field: index for index, field in enumerate(field_names)}
    class_field_ids = torch.tensor([field_to_id[key.split("\t", 1)[0]] for key in class_keys], dtype=torch.long)

    train_set = FieldDataset(args.dataset, rows, "train", class_to_id, field_to_id, args.height, args.max_train, args.seed)
    val_set = FieldDataset(args.dataset, rows, "val", class_to_id, field_to_id, args.height, args.max_val, args.seed + 1)
    test_set = FieldDataset(args.dataset, rows, "test", class_to_id, field_to_id, args.height, args.max_test, args.seed + 2)
    train_loader = DataLoader(train_set, batch_size=args.batch_size, shuffle=True, collate_fn=collate, num_workers=0)
    val_loader = DataLoader(val_set, batch_size=args.batch_size, shuffle=False, collate_fn=collate, num_workers=0)
    test_loader = DataLoader(test_set, batch_size=args.batch_size, shuffle=False, collate_fn=collate, num_workers=0)

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model = FieldAwareClassifier(len(class_keys), len(field_names), args.base_channels, args.field_embedding).to(device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    class_field_ids = class_field_ids.to(device)
    weights = None if args.no_class_weights else class_weights(train_set, len(class_keys)).to(device)
    criterion = nn.CrossEntropyLoss(weight=weights)
    args.out_dir.mkdir(parents=True, exist_ok=True)

    history = []
    best = {"exactRate": -1.0, "epoch": 0}
    for epoch in range(1, args.epochs + 1):
        model.train()
        losses = []
        for batch in train_loader:
            images = batch["images"].to(device)
            field_ids = batch["fieldIds"].to(device)
            targets = batch["targets"].to(device)
            logits = mask_logits(model(images, field_ids), field_ids, class_field_ids)
            loss = criterion(logits, targets)
            optimizer.zero_grad(set_to_none=True)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
            optimizer.step()
            losses.append(float(loss.detach().cpu()))
        val_metrics = evaluate(model, val_loader, class_keys, class_field_ids, device)
        record = {
            "epoch": epoch,
            "trainLoss": sum(losses) / max(1, len(losses)),
            "val": val_metrics,
        }
        history.append(record)
        print(json.dumps({"epoch": epoch, "trainLoss": record["trainLoss"], "valExact": val_metrics["exactRate"]}, ensure_ascii=False), flush=True)
        if val_metrics["exactRate"] > best["exactRate"]:
            best = {"exactRate": val_metrics["exactRate"], "epoch": epoch}
            torch.save({
                "model": model.state_dict(),
                "classes": class_keys,
                "fields": field_names,
                "epoch": epoch,
            }, args.out_dir / "best.pt")

    if (args.out_dir / "best.pt").exists():
        checkpoint = torch.load(args.out_dir / "best.pt", map_location=device)
        model.load_state_dict(checkpoint["model"])

    metrics = {
        "device": str(device),
        "height": args.height,
        "baseChannels": args.base_channels,
        "fields": field_names,
        "classCount": len(class_keys),
        "fieldMasked": True,
        "trainSamples": len(train_set),
        "valSamples": len(val_set),
        "testSamples": len(test_set),
        "best": best,
        "history": history,
        "val": evaluate(model, val_loader, class_keys, class_field_ids, device),
        "test": evaluate(model, test_loader, class_keys, class_field_ids, device),
    }
    (args.out_dir / "metrics.json").write_text(json.dumps(metrics, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"final": {"best": best, "testExact": metrics["test"]["exactRate"]}}, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
