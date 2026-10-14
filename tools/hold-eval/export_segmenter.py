"""Exports the hold segmenter (MobileSAM, Apache-2.0) to the two ONNX files the app loads.

    pip install torch onnx "mobile_sam @ git+https://github.com/ChaoningZhang/MobileSAM.git"
    curl -LO https://github.com/ChaoningZhang/MobileSAM/raw/master/weights/mobile_sam.pt
    python export_segmenter.py mobile_sam.pt <out folder>

Writes hold-segmenter-encoder.onnx (image, RGB 0..255 as float32 [1, 3, 1024, 1024] -> embedding [1, 256, 64, 64];
the normalisation is inside the model) and hold-segmenter-decoder.onnx (embedding + prompt points in the 1024 frame ->
low_res_masks [1, 1, 256, 256] logits and iou_predictions). The files are not kept in the repository (*.onnx is
ignored): the app looks for them next to the detector model and outlines the old way when they are missing.
"""
import pathlib
import sys
import warnings

import torch
from mobile_sam import sam_model_registry
from mobile_sam.utils.onnx import SamOnnxModel

OPSET = 17


class Encoder(torch.nn.Module):
    """The image encoder with SAM's pixel normalisation in front, so the caller passes plain RGB values."""

    def __init__(self, sam):
        super().__init__()
        self.encoder = sam.image_encoder
        self.register_buffer("mean", sam.pixel_mean.view(1, 3, 1, 1))
        self.register_buffer("std", sam.pixel_std.view(1, 3, 1, 1))

    def forward(self, image):
        return self.encoder((image - self.mean) / self.std)


def main() -> None:
    checkpoint, out = sys.argv[1], pathlib.Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)
    sam = sam_model_registry["vit_t"](checkpoint=checkpoint).eval()
    warnings.filterwarnings("ignore")
    with torch.no_grad():
        torch.onnx.export(
            Encoder(sam), torch.zeros(1, 3, 1024, 1024), str(out / "hold-segmenter-encoder.onnx"),
            input_names=["image"], output_names=["embedding"], opset_version=OPSET, do_constant_folding=True, dynamo=False)
        decoder = SamOnnxModel(sam, return_single_mask=True)
        size = sam.prompt_encoder.image_embedding_size
        inputs = {
            "image_embeddings": torch.randn(1, sam.prompt_encoder.embed_dim, *size),
            "point_coords": torch.randint(0, 1024, (1, 3, 2), dtype=torch.float),
            "point_labels": torch.tensor([[2.0, 3.0, -1.0]]),
            "mask_input": torch.zeros(1, 1, 4 * size[0], 4 * size[1]),
            "has_mask_input": torch.zeros(1),
            "orig_im_size": torch.tensor([1024.0, 1024.0]),
        }
        torch.onnx.export(
            decoder, tuple(inputs.values()), str(out / "hold-segmenter-decoder.onnx"),
            input_names=list(inputs), output_names=["masks", "iou_predictions", "low_res_masks"],
            dynamic_axes={"point_coords": {1: "points"}, "point_labels": {1: "points"}},
            opset_version=OPSET, do_constant_folding=True, dynamo=False)
    for name in ("hold-segmenter-encoder.onnx", "hold-segmenter-decoder.onnx"):
        print(name, f"{(out / name).stat().st_size / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
