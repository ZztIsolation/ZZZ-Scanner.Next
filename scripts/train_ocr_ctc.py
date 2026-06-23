import argparse
import json
import math
import random
from pathlib import Path
from typing import Any

import torch
from PIL import Image
from torch import nn
from torch.utils.data import DataLoader, Dataset
from torchvision import transforms


class RoiDataset(Dataset):
    def __init__(
        self,
        root: Path,
        split: str,
        charset: list[str],
        include_review: bool = False,
        image_height: int = 48,
        max_samples: int = 0,
        seed: int = 20260624,
        fields: set[str] | None = None,
    ):
        self.root = root
        self.blank_index = 0
        self.char_to_id = {ch: i for i, ch in enumerate(charset)}
        self.image_height = image_height
        rows = []
        with (root / "manifest.jsonl").open("r", encoding="utf-8") as handle:
            for line in handle:
                row = json.loads(line)
                if row["split"] != split:
                    continue
                if row.get("needsReview") and not include_review:
                    continue
                if not row.get("label"):
                    continue
                if fields and row.get("field") not in fields:
                    continue
                rows.append(row)
        if max_samples > 0 and len(rows) > max_samples:
            rng = random.Random(seed)
            rng.shuffle(rows)
            rows = rows[:max_samples]
        self.rows = rows
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
        tensor = self.transform(image)
        label = row["label"]
        target = torch.tensor([self.char_to_id[ch] for ch in label], dtype=torch.long)
        return {"image": tensor, "target": target, "label": label, "row": row}


def collate(batch: list[dict[str, Any]]) -> dict[str, Any]:
    max_width = max(item["image"].shape[-1] for item in batch)
    images = []
    targets = []
    target_lengths = []
    labels = []
    rows = []
    for item in batch:
        image = item["image"]
        pad = max_width - image.shape[-1]
        if pad > 0:
            image = torch.nn.functional.pad(image, (0, pad), value=-1.0)
        images.append(image)
        targets.append(item["target"])
        target_lengths.append(len(item["target"]))
        labels.append(item["label"])
        rows.append(item["row"])
    return {
        "images": torch.stack(images),
        "targets": torch.cat(targets),
        "target_lengths": torch.tensor(target_lengths, dtype=torch.long),
        "labels": labels,
        "rows": rows,
    }


class TinyCtcOcr(nn.Module):
    def __init__(self, classes: int, base_channels: int = 16):
        super().__init__()
        c1 = base_channels
        c2 = base_channels * 2
        c3 = base_channels * 4
        c4 = base_channels * 6
        c5 = base_channels * 8
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
            nn.MaxPool2d((2, 1)),
            nn.Conv2d(c4, c5, 3, padding=1),
            nn.BatchNorm2d(c5),
            nn.ReLU(inplace=True),
        )
        self.classifier = nn.Linear(c5, classes)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        feats = self.features(x)
        feats = feats.mean(dim=2)
        feats = feats.permute(2, 0, 1)
        return self.classifier(feats).log_softmax(dim=2)


def decode(log_probs: torch.Tensor, charset: list[str]) -> list[str]:
    pred = log_probs.argmax(dim=2).transpose(0, 1)
    output = []
    for seq in pred:
        chars = []
        previous = -1
        for token in seq.tolist():
            if token != 0 and token != previous:
                chars.append(charset[token])
            previous = token
        output.append("".join(chars))
    return output


def edit_distance(a: str, b: str) -> int:
    dp = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        prev = dp[0]
        dp[0] = i
        for j, cb in enumerate(b, 1):
            old = dp[j]
            dp[j] = prev if ca == cb else min(prev, dp[j], dp[j - 1]) + 1
            prev = old
    return dp[-1]


@torch.no_grad()
def evaluate(model: nn.Module, loader: DataLoader, charset: list[str], device: torch.device, limit_examples: int = 20) -> dict[str, Any]:
    model.eval()
    total = 0
    exact = 0
    edit = 0
    chars = 0
    examples = []
    by_field: dict[str, dict[str, int]] = {}
    for batch in loader:
        images = batch["images"].to(device)
        predictions = decode(model(images), charset)
        for pred, label, row in zip(predictions, batch["labels"], batch["rows"]):
            total += 1
            exact += int(pred == label)
            edit += edit_distance(pred, label)
            chars += max(1, len(label))
            field = row["field"]
            stats = by_field.setdefault(field, {"total": 0, "exact": 0})
            stats["total"] += 1
            stats["exact"] += int(pred == label)
            if len(examples) < limit_examples and pred != label:
                examples.append({"field": field, "label": label, "prediction": pred, "image": row["image"]})
    return {
        "total": total,
        "exact": exact,
        "exactRate": exact / total if total else 0.0,
        "cer": edit / chars if chars else 0.0,
        "byField": {k: {"total": v["total"], "exactRate": v["exact"] / v["total"]} for k, v in sorted(by_field.items())},
        "examples": examples,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Train a tiny CTC OCR prototype.")
    parser.add_argument("--dataset", required=True, type=Path)
    parser.add_argument("--out-dir", required=True, type=Path)
    parser.add_argument("--epochs", default=8, type=int)
    parser.add_argument("--batch-size", default=64, type=int)
    parser.add_argument("--lr", default=1e-3, type=float)
    parser.add_argument("--seed", default=20260624, type=int)
    parser.add_argument("--include-review", action="store_true")
    parser.add_argument("--height", default=48, type=int)
    parser.add_argument("--base-channels", default=16, type=int)
    parser.add_argument("--max-train", default=0, type=int)
    parser.add_argument("--max-val", default=0, type=int)
    parser.add_argument("--max-test", default=0, type=int)
    parser.add_argument("--fields", default="", help="Comma-separated field names to train on.")
    parser.add_argument("--threads", default=0, type=int)
    args = parser.parse_args()

    random.seed(args.seed)
    torch.manual_seed(args.seed)
    if args.threads > 0:
        torch.set_num_threads(args.threads)
    charset = (args.dataset / "charset.txt").read_text(encoding="utf-8").splitlines()
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    fields = {field.strip() for field in args.fields.split(",") if field.strip()} or None
    train_set = RoiDataset(args.dataset, "train", charset, include_review=args.include_review, image_height=args.height, max_samples=args.max_train, seed=args.seed, fields=fields)
    val_set = RoiDataset(args.dataset, "val", charset, include_review=args.include_review, image_height=args.height, max_samples=args.max_val, seed=args.seed + 1, fields=fields)
    test_set = RoiDataset(args.dataset, "test", charset, include_review=args.include_review, image_height=args.height, max_samples=args.max_test, seed=args.seed + 2, fields=fields)
    train_loader = DataLoader(train_set, batch_size=args.batch_size, shuffle=True, collate_fn=collate, num_workers=0)
    val_loader = DataLoader(val_set, batch_size=args.batch_size, shuffle=False, collate_fn=collate, num_workers=0)
    test_loader = DataLoader(test_set, batch_size=args.batch_size, shuffle=False, collate_fn=collate, num_workers=0)

    model = TinyCtcOcr(len(charset), base_channels=args.base_channels).to(device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    criterion = nn.CTCLoss(blank=0, zero_infinity=True)
    history = []
    best = {"cer": math.inf, "epoch": 0}
    args.out_dir.mkdir(parents=True, exist_ok=True)

    for epoch in range(1, args.epochs + 1):
        model.train()
        losses = []
        for batch in train_loader:
            images = batch["images"].to(device)
            targets = batch["targets"].to(device)
            target_lengths = batch["target_lengths"].to(device)
            log_probs = model(images)
            input_lengths = torch.full((images.shape[0],), log_probs.shape[0], dtype=torch.long, device=device)
            loss = criterion(log_probs, targets, input_lengths, target_lengths)
            optimizer.zero_grad(set_to_none=True)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
            optimizer.step()
            losses.append(float(loss.detach().cpu()))
        val_metrics = evaluate(model, val_loader, charset, device)
        record = {
            "epoch": epoch,
            "trainLoss": sum(losses) / max(1, len(losses)),
            "val": val_metrics,
        }
        history.append(record)
        print(json.dumps({"epoch": epoch, "trainLoss": record["trainLoss"], "valExact": val_metrics["exactRate"], "valCer": val_metrics["cer"]}, ensure_ascii=False), flush=True)
        if val_metrics["cer"] < best["cer"]:
            best = {"cer": val_metrics["cer"], "epoch": epoch}
            torch.save({"model": model.state_dict(), "charset": charset, "epoch": epoch}, args.out_dir / "best.pt")

    if (args.out_dir / "best.pt").exists():
        checkpoint = torch.load(args.out_dir / "best.pt", map_location=device)
        model.load_state_dict(checkpoint["model"])
    metrics = {
        "device": str(device),
        "height": args.height,
        "baseChannels": args.base_channels,
        "fields": sorted(fields) if fields else None,
        "trainSamples": len(train_set),
        "valSamples": len(val_set),
        "testSamples": len(test_set),
        "charsetSize": len(charset),
        "best": best,
        "history": history,
        "val": evaluate(model, val_loader, charset, device),
        "test": evaluate(model, test_loader, charset, device),
    }
    (args.out_dir / "metrics.json").write_text(json.dumps(metrics, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"final": {"best": best, "testExact": metrics["test"]["exactRate"], "testCer": metrics["test"]["cer"]}}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
