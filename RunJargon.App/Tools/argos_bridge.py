import json
import logging
import sys

logging.getLogger().setLevel(logging.ERROR)
logging.getLogger("stanza").setLevel(logging.ERROR)


def fail(message: str) -> None:
    sys.stderr.write(message)
    sys.exit(1)


def read_request_payload(raw: bytes) -> dict:
    if not raw:
        return {}

    try:
        text = raw.decode("utf-8-sig")
    except Exception as exc:
        fail(f"Cannot decode request payload: {exc}")

    try:
        return json.loads(text)
    except Exception as exc:
        fail(f"Cannot read request payload: {exc}")


TRANSLATION_CACHE = {}
INSTALLED_LANGUAGES = None


def refresh_installed_languages():
    global INSTALLED_LANGUAGES

    import argostranslate.translate

    INSTALLED_LANGUAGES = argostranslate.translate.get_installed_languages()
    return INSTALLED_LANGUAGES


def ensure_package(from_code: str, to_code: str):
    import argostranslate.package

    installed_languages = refresh_installed_languages()
    for language in installed_languages:
        if language.code != from_code:
            continue

        for translation in language.translations_from:
            if translation.to_lang.code == to_code:
                return

    argostranslate.package.update_package_index()
    available_packages = argostranslate.package.get_available_packages()

    for package in available_packages:
        if package.from_code == from_code and package.to_code == to_code:
            download_path = package.download()
            argostranslate.package.install_from_path(download_path)
            refresh_installed_languages()
            return

    fail(f"Argos model {from_code}->{to_code} is not available.")


def get_translation(from_language: str, to_language: str):
    cache_key = f"{from_language}->{to_language}"
    if cache_key in TRANSLATION_CACHE:
        return TRANSLATION_CACHE[cache_key]

    ensure_package(from_language, to_language)

    installed_languages = INSTALLED_LANGUAGES or refresh_installed_languages()
    from_lang = next((language for language in installed_languages if language.code == from_language), None)
    to_lang = next((language for language in installed_languages if language.code == to_language), None)

    if from_lang is None or to_lang is None:
        fail(f"Installed language pair {from_language}->{to_language} is missing.")

    translation = from_lang.get_translation(to_lang)
    TRANSLATION_CACHE[cache_key] = translation
    return translation


def process_request(payload: dict) -> dict:
    mode = payload.get("Mode", "translate")
    text = payload.get("Text", "")
    texts = payload.get("Texts", [])
    from_language = payload.get("FromLanguage", "en")
    to_language = payload.get("ToLanguage", "ru")

    translation = get_translation(from_language, to_language)

    if mode == "warmup":
        translation.translate("Hello world")
        return {
            "TranslatedText": "",
            "Note": "Argos warmup completed."
        }

    if mode == "translate_batch":
        if not isinstance(texts, list):
            raise ValueError("Texts must be an array for translate_batch.")

        translated_texts = []
        for item in texts:
            value = "" if item is None else str(item)
            translated_texts.append("" if not value.strip() else translation.translate(value))

        return {
            "TranslatedTexts": translated_texts,
            "Note": "Пакетный перевод выполнен локально через Argos Translate."
        }

    if not text.strip():
        return {
            "TranslatedText": "",
            "Note": "Пустой текст для перевода."
        }

    translated = translation.translate(text)
    return {
        "TranslatedText": translated,
        "Note": "Перевод выполнен локально через Argos Translate."
    }


def serve() -> None:
    while True:
        raw_line = sys.stdin.buffer.readline()
        if not raw_line:
            break

        try:
            payload = read_request_payload(raw_line)
            response = process_request(payload)
        except Exception as exc:
            response = {"Error": str(exc)}

        sys.stdout.write(json.dumps(response, ensure_ascii=False) + "\n")
        sys.stdout.flush()


def main() -> None:
    if len(sys.argv) > 1 and sys.argv[1] == "--serve":
        serve()
        return

    payload = read_request_payload(sys.stdin.buffer.read())

    try:
        response = process_request(payload)
        print(json.dumps(response, ensure_ascii=False))
    except Exception as exc:
        fail(str(exc))


if __name__ == "__main__":
    main()
