import json, os
import torch
import torchvision
from torchvision.models.detection.faster_rcnn import FastRCNNPredictor
from PIL import Image

class TumorDataset(torch.utils.data.Dataset):
    def __init__(self, split_file):
        with open(split_file) as f:
            self.data = json.load(f)

    def __len__(self):
        return len(self.data)

    def __getitem__(self, idx):
        entry = self.data[idx]
        img = Image.open(entry["png_path"]).convert("RGB")
        img = torchvision.transforms.functional.to_tensor(img)

        boxes = []
        labels = []
        for b in entry["boxes"]:
            boxes.append([b["xmin"], b["ymin"], b["xmax"], b["ymax"]])
            labels.append(1)

        if len(boxes) == 0:
            boxes = torch.zeros((0, 4), dtype=torch.float32)
            labels = torch.zeros((0,), dtype=torch.int64)
        else:
            boxes = torch.tensor(boxes, dtype=torch.float32)
            labels = torch.tensor(labels, dtype=torch.int64)

        target = {"boxes": boxes, "labels": labels, "image_id": torch.tensor([idx])}
        return img, target

def collate_fn(batch):
    return tuple(zip(*batch))

train_ds = TumorDataset("train_split_v2.json")
train_loader = torch.utils.data.DataLoader(train_ds, batch_size=2, shuffle=True, collate_fn=collate_fn)

model = torchvision.models.detection.fasterrcnn_resnet50_fpn(weights="DEFAULT")
num_classes = 2
in_features = model.roi_heads.box_predictor.cls_score.in_features
model.roi_heads.box_predictor = FastRCNNPredictor(in_features, num_classes)

device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
model.to(device)
print(f"Using device: {device}")
print(f"Training on {len(train_ds)} slices")

optimizer = torch.optim.SGD(model.parameters(), lr=0.005, momentum=0.9, weight_decay=0.0005)

num_epochs = 10
model.train()
for epoch in range(num_epochs):
    total_loss = 0
    for images, targets in train_loader:
        images = [img.to(device) for img in images]
        targets = [{k: v.to(device) for k, v in t.items()} for t in targets]

        loss_dict = model(images, targets)
        loss = sum(l for l in loss_dict.values())

        optimizer.zero_grad()
        loss.backward()
        optimizer.step()
        total_loss += loss.item()

    print(f"Epoch {epoch+1}/{num_epochs} - Loss: {total_loss:.4f}")

torch.save(model.state_dict(), "tumor_model_v4.pth")
print("Saved model to tumor_model_v4.pth")