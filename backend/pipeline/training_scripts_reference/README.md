# Why these files are here

`process_all_patients.py` and `train_model_v2.py` are the exact scripts
used to build the dataset and train `tumor_model_v4.pth`. They're kept
here (not executed by the app) purely as a reference, because
`tumor_infer.py`'s preprocessing was written by directly reading this
code line-by-line - not guessed. If the model is retrained differently
in the future, update `backend/pipeline/tumor_infer.py`'s
`preprocess_slice()` to match whatever changes, and drop the new
training script(s) in here too so the next person (or the next Claude
session) has the same ground truth to work from instead of guessing.

Specifically, `tumor_infer.py`'s `preprocess_slice()` mirrors:
- `process_all_patients.py`: per-slice min-max normalize to 0-255,
  no HU windowing, no resize (native DICOM resolution is used as-is).
- `train_model_v2.py`'s `TumorDataset.__getitem__`: grayscale -> RGB
  via `.convert("RGB")`, then `to_tensor()` (uint8 [0,255] -> float [0,1]).

If you ever see the words "assumption" or "not guaranteed" near
preprocessing code again, it means whoever's reading this doesn't have
the current training script - get it from whoever trained the model
and update accordingly, the same way this fix was made.
