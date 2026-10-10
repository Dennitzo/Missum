from pathlib import Path
import hashlib

import pytest
from fastapi import HTTPException
from PIL import Image

import app


def test_webp_is_decoded_to_llama_compatible_jpeg(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.setattr(app, "DATA_ROOT", tmp_path)
    monkeypatch.setattr(app, "OUTPUT_ROOT", tmp_path / "output")
    source = tmp_path / "reference.webp"
    Image.new("RGBA", (640, 360), (32, 96, 160, 180)).save(source, "WEBP")

    result = app._inspect_image(source)

    vision = next(item for item in result["artifacts"] if item["role"] == "vision_input")
    vision_path = app.DATA_ROOT / vision["relativePath"]
    with Image.open(vision_path) as decoded:
        assert decoded.format == "JPEG"
        assert decoded.size == (640, 360)
    assert vision["mediaType"] == "image/jpeg"


@pytest.mark.parametrize("image_format,extension,media_type", [
    ("PNG", ".png", "image/png"),
    ("JPEG", ".jpg", "image/jpeg"),
    ("WEBP", ".webp", "image/webp"),
])
def test_original_is_byte_exact_while_model_and_thumbnail_are_resized(
    tmp_path: Path, monkeypatch, image_format: str, extension: str, media_type: str,
) -> None:
    monkeypatch.setattr(app, "DATA_ROOT", tmp_path)
    monkeypatch.setattr(app, "OUTPUT_ROOT", tmp_path / "output")
    source = tmp_path / "payload.bin"
    Image.new("RGB", (5000, 1500), (32, 96, 160)).save(source, image_format)
    original_bytes = source.read_bytes()

    result = app._inspect_image(source)

    original = next(item for item in result["artifacts"] if item["role"] == "original")
    original_path = app.DATA_ROOT / original["relativePath"]
    assert original["mediaType"] == media_type
    assert original["fileName"] == "original" + extension
    assert original_path.read_bytes() == original_bytes
    assert hashlib.sha256(original_path.read_bytes()).digest() == hashlib.sha256(original_bytes).digest()
    with Image.open(original_path) as decoded:
        assert decoded.size == (5000, 1500)
    vision = next(item for item in result["artifacts"] if item["role"] == "vision_input")
    with Image.open(app.DATA_ROOT / vision["relativePath"]) as decoded:
        assert decoded.format == "JPEG"
        assert max(decoded.size) == 4096
    thumbnail = next(item for item in result["artifacts"] if item["role"] == "thumbnail")
    with Image.open(app.DATA_ROOT / thumbnail["relativePath"]) as decoded:
        assert decoded.format == "JPEG"
        assert max(decoded.size) == 512
    assert result["metadata"]["width"] == 5000
    assert result["metadata"]["height"] == 1500


def test_original_preserves_alpha_and_png_container(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.setattr(app, "DATA_ROOT", tmp_path)
    monkeypatch.setattr(app, "OUTPUT_ROOT", tmp_path / "output")
    source = tmp_path / "payload.bin"
    Image.new("RGBA", (240, 120), (20, 40, 60, 100)).save(source, "PNG")

    result = app._inspect_image(source)

    original = next(item for item in result["artifacts"] if item["role"] == "original")
    original_path = app.DATA_ROOT / original["relativePath"]
    assert original_path.read_bytes() == source.read_bytes()
    with Image.open(original_path) as decoded:
        assert decoded.mode == "RGBA"
        assert decoded.getpixel((0, 0)) == (20, 40, 60, 100)


def test_corrupt_image_is_rejected_before_native_model_request(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.setattr(app, "DATA_ROOT", tmp_path)
    monkeypatch.setattr(app, "OUTPUT_ROOT", tmp_path / "output")
    source = tmp_path / "broken.webp"
    source.write_bytes(b"RIFF-not-an-image-WEBP")

    with pytest.raises(HTTPException) as error:
        app._inspect_image(source)

    assert error.value.status_code == 400
    assert error.value.detail == {"errorCode": "media.invalid_image"}


def test_png_with_bad_chunk_checksum_is_reported_as_invalid_input(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.setattr(app, "DATA_ROOT", tmp_path)
    monkeypatch.setattr(app, "OUTPUT_ROOT", tmp_path / "output")
    source = tmp_path / "broken.png"
    Image.new("RGB", (128, 128), "red").save(source, "PNG")
    data = bytearray(source.read_bytes())
    position = data.index(b"IDAT")
    data[position + 4] ^= 1
    source.write_bytes(data)

    with pytest.raises(HTTPException) as error:
        app._inspect_image(source)

    assert error.value.status_code == 400
    assert error.value.detail == {"errorCode": "media.invalid_image"}
