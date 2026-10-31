from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from pathlib import Path
from tempfile import TemporaryDirectory
from threading import Event
from types import SimpleNamespace
from unittest import TestCase, main
from unittest.mock import call, patch
import wave

import numpy as np

import app


def part(index, text, speed=1.0, before=0, after=0):
    return app.SpeechParagraphPart(
        segmentIndex=index,
        text=text,
        speed=speed,
        pauseBeforeMilliseconds=before,
        pauseAfterMilliseconds=after,
    )


@contextmanager
def paragraph_environment():
    # Exercise actual PCM/WAV output, but never load or execute a speech model.
    with TemporaryDirectory(prefix="missum-synthesis-") as temporary:
        root = Path(temporary)
        session = SimpleNamespace(
            provider="supertonic-test-fixture", cancel_event=Event()
        )
        with patch.object(app, "DATA_ROOT", root), \
             patch.object(app, "OUTPUT_ROOT", root / "artifacts" / "worker"), \
             patch.object(app, "_require_speech_session", return_value=session), \
             patch.object(app, "_acquire_process_slots", return_value=["fixture"]), \
             patch.object(app, "_release_process_slots") as release, \
             patch.object(app.models, "record_tts_error"), \
             patch.object(app.models, "current_tts_precision", return_value="fixture"), \
             patch.object(app.models, "current_tts_execution_provider", return_value="fixture"):
            yield root, session, release


class SpeechParagraphPartsTests(TestCase):
    def test_part_speed_and_requested_silence_are_preserved_in_pcm_and_timings(self):
        parts = [
            part(4, "Erster Satz.", 0.8, before=100, after=200),
            part(9, "Zweiter Satz.", 0.9, before=300, after=400),
        ]
        cancel = Event()
        with patch.object(app, "_synthesize_supertonic", side_effect=[
            (np.full(100, 0.25, dtype=np.float32), 1000),
            (np.full(50, 0.5, dtype=np.float32), 1000),
        ]) as synthesize:
            pcm, sample_rate, timings = app._synthesize_paragraph_parts(parts, cancel)

        self.assertEqual([
            call("Erster Satz.", 0.8, cancel),
            call("Zweiter Satz.", 0.9, cancel),
        ], synthesize.call_args_list)
        self.assertEqual(1000, sample_rate)
        # Both requested boundary pauses apply; no extra technical pause is added.
        self.assertEqual(1150, pcm.size)
        np.testing.assert_array_equal(pcm[:100], np.zeros(100))
        np.testing.assert_array_equal(pcm[100:200], np.full(100, 0.25))
        np.testing.assert_array_equal(pcm[200:700], np.zeros(500))
        np.testing.assert_array_equal(pcm[700:750], np.full(50, 0.5))
        np.testing.assert_array_equal(pcm[750:], np.zeros(400))
        self.assertEqual([4, 9], [timing["segmentIndex"] for timing in timings])
        self.assertAlmostEqual(0.1, timings[0]["startSeconds"])
        self.assertAlmostEqual(0.2, timings[0]["endSeconds"])
        self.assertAlmostEqual(0.7, timings[1]["startSeconds"])
        self.assertAlmostEqual(0.75, timings[1]["endSeconds"])

    def test_technical_gap_is_used_only_without_requested_boundary_silence(self):
        parts = [part(0, "Eins."), part(1, "Zwei.")]
        with patch.object(app, "_synthesize_supertonic", return_value=(
            np.full(100, 0.25, dtype=np.float32), 1000
        )):
            pcm, _, timings = app._synthesize_paragraph_parts(parts, Event())
        self.assertEqual(220, pcm.size)
        np.testing.assert_array_equal(pcm[100:120], np.zeros(20))
        self.assertAlmostEqual(0.12, timings[1]["startSeconds"])
        self.assertAlmostEqual(0.22, timings[1]["endSeconds"])

    def test_explicit_zero_length_edge_does_not_suppress_other_part_pause(self):
        parts = [part(0, "Eins.", after=150), part(1, "Zwei.")]
        with patch.object(app, "_synthesize_supertonic", return_value=(
            np.full(100, 0.25, dtype=np.float32), 1000
        )):
            pcm, _, timings = app._synthesize_paragraph_parts(parts, Event())
        self.assertEqual(350, pcm.size)
        self.assertAlmostEqual(0.25, timings[1]["startSeconds"])

    def test_cancellation_is_propagated_without_synthesizing_later_parts(self):
        parts = [part(0, "Eins."), part(1, "Zwei.")]
        cancel = Event()
        with patch.object(app, "_synthesize_supertonic", side_effect=InterruptedError("cancelled")) as synthesize:
            with self.assertRaises(InterruptedError):
                app._synthesize_paragraph_parts(parts, cancel)
        synthesize.assert_called_once_with("Eins.", 1.0, cancel)


class SpeechParagraphOutputTests(TestCase):
    def test_requested_speed_reaches_model_in_the_normal_whole_paragraph_path(self):
        request = app.SpeechParagraphRequest(
            sessionId="speech-" + "a" * 32,
            paragraphIndex=0,
            text="Das ist ein langsamer Satz.",
            speed=0.8,
        )
        with paragraph_environment() as (root, session, release), \
             patch.object(app, "_synthesize_supertonic", return_value=(
                 np.full(8820, 0.25, dtype=np.float32), 44100
             )) as synthesize, \
             patch.object(app, "_synthesize_paragraph_parts") as segmented:
            result = app._synthesize_paragraph(request)
            with wave.open(str(root / result["relativePath"]), "rb") as output:
                self.assertEqual(44100, output.getframerate())
                self.assertEqual(8820, output.getnframes())
                self.assertEqual(2, output.getsampwidth())
        synthesize.assert_called_once_with(request.text, 0.8, session.cancel_event)
        segmented.assert_not_called()
        release.assert_called_once_with(["fixture"])

    def test_mixed_part_speeds_automatically_select_segment_synthesis(self):
        parts = [part(0, "Eins.", 0.8), part(1, "Zwei.", 1.2)]
        request = app.SpeechParagraphRequest(
            sessionId="speech-" + "b" * 32, paragraphIndex=0,
            text="Eins. Zwei.", speed=1.0, parts=parts,
        )
        with paragraph_environment(), \
             patch.object(app, "_synthesize_supertonic", return_value=(
                 np.full(8820, 0.25, dtype=np.float32), 44100
             )) as synthesize:
            result = app._synthesize_paragraph(request)
        self.assertEqual([0.8, 1.2], [entry.args[1] for entry in synthesize.call_args_list])
        self.assertEqual([0, 1], [timing["segmentIndex"] for timing in result["timings"]])

    def test_requested_pause_is_written_to_exported_wav_without_force_segment_flag(self):
        request = app.SpeechParagraphRequest(
            sessionId="speech-" + "c" * 32, paragraphIndex=0,
            text="Eins. Zwei.", speed=0.8,
            parts=[part(0, "Eins.", 0.8, after=600), part(1, "Zwei.", 0.8)],
        )
        with paragraph_environment() as (root, _, _), \
             patch.object(app, "_synthesize_supertonic", return_value=(
                 np.full(8820, 0.25, dtype=np.float32), 44100
             )):
            result = app._synthesize_paragraph(request)
            with wave.open(str(root / result["relativePath"]), "rb") as output:
                self.assertEqual(44100, output.getnframes())
                pcm = np.frombuffer(output.readframes(output.getnframes()), dtype=np.int16)
            np.testing.assert_array_equal(pcm[8820:35280], np.zeros(26460, dtype=np.int16))
        self.assertAlmostEqual(0.8, result["timings"][1]["startSeconds"])
        self.assertAlmostEqual(1.0, result["timings"][1]["endSeconds"])

    def test_cancelled_synthesis_does_not_leave_a_wav_and_releases_capacity(self):
        request = app.SpeechParagraphRequest(
            sessionId="speech-" + "d" * 32, paragraphIndex=0, text="Ein Satz.",
        )
        with paragraph_environment() as (root, _, release), \
             patch.object(app, "_synthesize_supertonic", side_effect=InterruptedError("cancelled")):
            with self.assertRaises(app.HTTPException) as error:
                app._synthesize_paragraph(request)
            self.assertEqual(409, error.exception.status_code)
            self.assertEqual("speech.session_cancelled", error.exception.detail["errorCode"])
            self.assertEqual([], list(root.rglob("*.wav")))
        release.assert_called_once_with(["fixture"])


class SpeechModelRegistryTests(TestCase):
    def test_schema_minimum_speed_uses_supported_provider_speed_and_retimes_audio(self):
        registry = app.ModelRegistry()
        processor, voice_style = object(), object()
        model = SimpleNamespace(
            model=SimpleNamespace(text_processor=processor), sample_rate=44100
        )
        audio = np.full(100, 0.25, dtype=np.float32)
        retimed = np.full(140, 0.25, dtype=np.float32)
        cancel = Event()
        # Retain the real serial owner-thread dispatch while replacing every
        # dependency that could initialize a GPU session or synthesize speech.
        with ThreadPoolExecutor(max_workers=1) as owner, \
             patch.object(app, "TTS_EXECUTOR", owner), \
             patch.object(registry, "_load_tts_on_owner_thread", return_value=(model, voice_style)) as load, \
             patch.object(app, "_normalize_supertonic_text", return_value="Normalisierter Text.") as normalize, \
             patch.object(app, "_synthesize_supertonic_cancellable", return_value=audio) as synthesize, \
             patch.object(app, "_retime_supertonic_audio", return_value=retimed) as retime:
            result, sample_rate = registry.synthesize_tts("Text.", 0.5, cancel)

        load.assert_called_once_with()
        normalize.assert_called_once_with("Text.", processor)
        synthesize.assert_called_once_with(
            model, voice_style, "Normalisierter Text.", 0.7, cancel
        )
        retime.assert_called_once_with(audio, 44100, 0.5 / 0.7)
        self.assertIs(retimed, result)
        self.assertEqual(44100, sample_rate)


class SpeechAudioRetimeTests(TestCase):
    def test_real_ffmpeg_slows_synthetic_pcm_without_lowering_pitch(self):
        sample_rate = 44100
        seconds = 2.0
        samples = np.arange(round(sample_rate * seconds)) / sample_rate
        source = (0.25 * np.sin(2 * np.pi * 440 * samples)).astype(np.float32)[None, :]
        ratio = 0.5 / 0.7

        retimed = app._retime_supertonic_audio(source, sample_rate, ratio)

        pcm = np.asarray(retimed, dtype=np.float32).reshape(-1)
        self.assertTrue(np.isfinite(pcm).all())
        self.assertAlmostEqual(seconds / ratio, pcm.size / sample_rate, delta=0.15)
        self.assertGreater(float(np.sqrt(np.mean(np.square(pcm)))), 0.1)
        spectrum = np.abs(np.fft.rfft(pcm))
        frequencies = np.fft.rfftfreq(pcm.size, 1 / sample_rate)
        self.assertAlmostEqual(440, float(frequencies[np.argmax(spectrum)]), delta=3)


if __name__ == "__main__":
    main()
