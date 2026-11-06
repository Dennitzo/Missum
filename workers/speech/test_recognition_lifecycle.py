from concurrent.futures import ThreadPoolExecutor
from threading import BoundedSemaphore, Event
from unittest import TestCase, main
from unittest.mock import Mock, patch

import app


class RecognitionLifecycleTests(TestCase):
    def test_configured_startup_preloads_only_tts(self):
        registry = Mock(spec=app.ModelRegistry)
        with patch.object(app, "models", registry):
            app._preload_models()

        registry.load_tts.assert_called_once_with()
        registry.load_stt.assert_not_called()
        registry.load_speaker.assert_not_called()

    def test_recognition_release_keeps_tts_model_and_sessions(self):
        registry = app.ModelRegistry()
        registry._stt = object()
        registry._speaker = object()
        tts = registry._tts = object()
        voice = registry._tts_voice_style = object()
        sessions = {"active-tts": Mock()}
        trackers = {"old-caption": Mock()}
        with patch.object(app, "models", registry), \
             patch.object(app, "SPEECH_SESSIONS", sessions), \
             patch.object(app, "SPEAKER_TRACKERS", trackers), \
             patch.object(app, "_empty_cuda_cache") as empty_cache:
            status = app.release_recognition()
            app.release_recognition()

        self.assertFalse(status["sttLoaded"])
        self.assertFalse(status["speakerLoaded"])
        self.assertFalse(status["recognitionReleasePending"])
        self.assertTrue(status["ttsLoaded"])
        self.assertIs(tts, registry._tts)
        self.assertIs(voice, registry._tts_voice_style)
        sessions["active-tts"].cancel_event.set.assert_not_called()
        self.assertFalse(trackers)
        empty_cache.assert_called_once_with(app.WHISPER_CUDA_DEVICE, app.SPEAKER_CUDA_DEVICE)

    def test_pending_release_drains_active_inference_before_a_new_load(self):
        registry = app.ModelRegistry()
        registry._stt = object()
        registry._speaker = object()
        active = Event()
        finish = Event()
        waiting = Event()
        loaded = Event()
        slots = BoundedSemaphore(app.MAX_PARALLEL_SPEECH_OPERATIONS)
        original_wait = registry._recognition_gate.wait_for

        def transcribe(*_):
            active.set()
            if not finish.wait(5):
                raise TimeoutError("Test did not allow the active recognition to finish")
            return {"text": "finished"}

        def wait_for(predicate):
            if not predicate():
                waiting.set()
            return original_wait(predicate)

        def warm():
            self.assertIsNone(registry._stt)
            self.assertIsNone(registry._speaker)
            registry._stt = object()
            loaded.set()

        with ThreadPoolExecutor(max_workers=2) as executor, \
             patch.object(app, "models", registry), \
             patch.object(app, "PROCESS_GATE", slots), \
             patch.object(app, "_transcribe_live_audio", side_effect=transcribe), \
             patch.object(app, "_empty_cuda_cache"), \
             patch.object(registry, "load_stt", side_effect=warm), \
             patch.object(registry._recognition_gate, "wait_for", side_effect=wait_for):
            recognition = executor.submit(
                app._transcribe_live_caption, b"audio", "de", "transcribe", None,
                "caption-" + "a" * 32, "dictation", True,
            )
            try:
                self.assertTrue(active.wait(5))
                status = app.release_recognition()
                self.assertTrue(status["recognitionReleasePending"])
                self.assertTrue(status["sttLoaded"])
                self.assertTrue(status["speakerLoaded"])
                loading = executor.submit(app.load, app.LoadRequest(component="stt"))
                self.assertTrue(waiting.wait(5))
                self.assertFalse(loaded.is_set())
            finally:
                finish.set()

            self.assertEqual({"text": "finished"}, recognition.result(timeout=5))
            status = loading.result(timeout=5)

        self.assertTrue(loaded.is_set())
        self.assertTrue(status["sttLoaded"])
        self.assertFalse(status["speakerLoaded"])
        self.assertFalse(status["recognitionReleasePending"])


if __name__ == "__main__":
    main()
