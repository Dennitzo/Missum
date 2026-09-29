from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace
from unittest import TestCase, main
from unittest.mock import Mock, patch

import app


class TranscriptionUploadTests(TestCase):
    def test_transcription_reads_the_same_lowercase_upload_directory_as_gateway(self):
        with TemporaryDirectory(prefix="missum-transcription-") as temporary:
            root = Path(temporary)
            upload_id = "upload-" + "a" * 32
            payload = root / "uploads" / upload_id / "payload.bin"
            payload.parent.mkdir(parents=True)
            payload.write_bytes(b"isolated-transcription-fixture")
            model = Mock()
            model.transcribe.return_value = (
                [SimpleNamespace(start=0.0, end=1.0, text=" Apfel ")],
                SimpleNamespace(language="de", language_probability=0.99),
            )
            with patch.object(app, "DATA_ROOT", root), \
                 patch.object(app.models, "load_stt", return_value=model), \
                 patch.object(app, "_acquire_process_slots", return_value=["fixture"]), \
                 patch.object(app, "_release_process_slots") as release:
                result = app.transcribe(app.TranscriptionRequest(uploadId=upload_id, language="de"))

            self.assertEqual("Apfel", result["text"])
            self.assertEqual("whisper-large-v3", result["provider"])
            self.assertEqual(str(payload.resolve()), model.transcribe.call_args.args[0])
            release.assert_called_once_with(["fixture"])

    def test_missing_upload_does_not_load_a_model_and_releases_slots(self):
        with TemporaryDirectory(prefix="missum-transcription-") as temporary:
            root = Path(temporary)
            (root / "uploads").mkdir()
            with patch.object(app, "DATA_ROOT", root), \
                 patch.object(app.models, "load_stt") as load, \
                 patch.object(app, "_acquire_process_slots", return_value=["fixture"]), \
                 patch.object(app, "_release_process_slots") as release:
                with self.assertRaises(FileNotFoundError):
                    app.transcribe(app.TranscriptionRequest(uploadId="upload-" + "b" * 32, language="de"))
            load.assert_not_called()
            release.assert_called_once_with(["fixture"])


if __name__ == "__main__":
    main()
