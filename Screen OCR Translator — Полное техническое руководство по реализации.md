# Screen OCR Translator — Полное техническое руководство по реализации

## Обзор задачи

Задача — создать приложение для Windows, которое:
1. По горячей клавише открывает режим выделения области экрана
2. Делает скриншот выделенной области
3. Распознаёт текст через OCR
4. Переводит его с английского на русский
5. Отображает перевод **поверх оригинального текста** в прозрачном наложении (overlay)

Главная проблема — **OCR не распознаёт отдельные изолированные слова** (кнопки, метки интерфейса, пункты меню), расположенные рядом, но не связанные в абзацы. Это классическая проблема «sparse text» (разреженного текста), и для неё существуют конкретные решения.

***

## Архитектура приложения

Приложение состоит из пяти модулей, которые работают последовательно:

```
[Hotkey Listener]
       ↓
[Region Selector]  ← прозрачное окно для выделения мышью
       ↓
[Screenshot Capture]  ← mss (быстро) или PIL
       ↓
[Image Preprocessing]  ← самый важный этап для UI-текста
       ↓
[OCR Engine]  ← EasyOCR или Windows OCR (WinRT)
       ↓
[Translation]  ← deep-translator (Google/DeepL)
       ↓
[Overlay Renderer]  ← прозрачное tkinter-окно поверх экрана
```

***

## Главная проблема: почему OCR не работает с UI-словами

### Почему это происходит

Tesseract по умолчанию работает в режиме **PSM 3 — «полная страница текста»**. Он ищет абзацы, строки, блоки. Когда на скриншоте есть отдельные слова типа «File», «Edit», «View» — Tesseract либо игнорирует их, либо выдаёт мусор.[^1][^2]

Дополнительные причины:
- Маленький размер текста (UI-элементы часто 11–14px)[^3]
- Низкий контраст (серый текст на сером фоне)
- Текст расположен на цветных кнопках, иконках, плашках
- Шрифты с антиалиасингом на малых размерах ломают OCR[^4]

### Решение 1: правильный режим PSM

Для интерфейсных элементов нужно использовать **PSM 11 — «Sparse text»** (разреженный текст):[^5][^1]

```python
config = '--oem 3 --psm 11'
text = pytesseract.image_to_string(image, config=config)
```

PSM 11 говорит Tesseract: «найди весь текст, где бы он ни находился, не ищи структуру». Именно этот режим подходит для меню, кнопок, тулбаров.[^6]

Альтернативные PSM-режимы для разных сценариев:

| Сценарий | PSM | Описание |
|---|---|---|
| Кнопки и отдельные слова (UI) | **11** | Sparse text — лучший для интерфейса[^5] |
| Одна строка текста | 7 | Single text line[^1] |
| Одно слово | 8 | Single word[^1] |
| Смешанный контент | 6 | Single uniform block[^1] |
| Субтитры, диалоги | 3 | Default auto[^5] |

### Решение 2: предобработка изображения (Image Preprocessing)

Это **самый важный шаг** для точности OCR. Следует применять цепочку преобразований:[^7][^8]

```python
import cv2
import numpy as np
from PIL import Image

def preprocess_for_ocr(pil_image):
    # 1. Апскейл x3 — минимум 300 DPI для OCR
    w, h = pil_image.size
    pil_image = pil_image.resize((w * 3, h * 3), Image.LANCZOS)
    
    # 2. Конвертация в numpy/OpenCV
    img = np.array(pil_image)
    
    # 3. Перевод в серый
    gray = cv2.cvtColor(img, cv2.COLOR_RGB2GRAY)
    
    # 4. Увеличение контраста (CLAHE)
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8,8))
    gray = clahe.apply(gray)
    
    # 5. Адаптивная бинаризация (не простой порог!)
    binary = cv2.adaptiveThreshold(
        gray, 255,
        cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
        cv2.THRESH_BINARY, 11, 2
    )
    
    # 6. Лёгкий денойз
    denoised = cv2.fastNlMeansDenoising(binary, h=10)
    
    # 7. Добавить белую рамку (КРИТИЧЕСКИ ВАЖНО для UI)
    bordered = cv2.copyMakeBorder(
        denoised, 20, 20, 20, 20,
        cv2.BORDER_CONSTANT, value=255
    )
    
    return Image.fromarray(bordered)
```

**Почему белая рамка критически важна:** официальная документация Tesseract прямо указывает, что добавление белой рамки к тексту, который слишком плотно обрезан, значительно улучшает распознавание. UI-элементы часто захвачены «впритык», и OCR не видит границы символов.[^9][^10]

**Апскейл до 300 DPI:** OCR-движки оптимизированы для изображений с разрешением 300 DPI. Экранный контент — обычно 72–96 DPI. Увеличение в 3× даёт близкое к оптимальному качество.[^7]

### Решение 3: выбор OCR-движка

| OCR Движок | Точность (UI/sparse) | Скорость | Оффлайн | Рекомендация |
|---|---|---|---|---|
| **Tesseract 5 (PSM 11)** | Средняя | Быстро | ✅ | Базовый вариант, требует тюнинга[^11] |
| **EasyOCR** | Выше Tesseract на «wild» тексте | Медленнее | ✅ | Лучше для сложных фонов[^12] |
| **PaddleOCR** | Сравним с Google Vision | Быстро с GPU | ✅ | Лучший баланс точность/скорость[^12] |
| **Windows OCR (WinRT)** | Высокая | Очень быстро | ✅ | Рекомендован Translumo[^13] |
| **GPT-4o Vision** | 95%+, понимает контекст | Медленно | ❌ | Лучшее качество, но платный[^14] |

**Вывод:** для изолированных UI-элементов рекомендуется использовать **EasyOCR** или **Windows OCR (WinRT)**, а не Tesseract по умолчанию. Проект Translumo (4.2k звёзд на GitHub) — лучший пример приложения этого класса — рекомендует Windows OCR как основной движок, а Tesseract называет «старым и медленным».[^13]

***

## Как получить bounding boxes (координаты каждого слова)

Это ключевой элемент — нужно знать, **где именно** на скриншоте находилось каждое слово, чтобы разместить перевод точно поверх него.

### С Tesseract

```python
import pytesseract
from pytesseract import Output
import pandas as pd

data = pytesseract.image_to_data(
    image, 
    config='--oem 3 --psm 11',
    output_type=Output.DICT
)

df = pd.DataFrame(data)
df = df[df['conf'] > 30]  # фильтр по уверенности
df = df[df['text'].str.strip() != '']

for _, row in df.iterrows():
    x, y, w, h = row['left'], row['top'], row['width'], row['height']
    word = row['text']
    # x, y — координаты в пикселях скриншота
    # нужно добавить offset выделенной области для перевода на экранные координаты
```

`image_to_data()` возвращает координаты каждого слова. Это позволяет разместить переведённый текст точно там, где было оригинальное слово.[^15][^16]

### С EasyOCR

```python
import easyocr

reader = easyocr.Reader(['en'])
results = reader.readtext(image)

for (bbox, text, confidence) in results:
    # bbox = [[x1,y1], [x2,y2], [x3,y3], [x4,y4]] — четыре угла
    top_left = bbox
    bottom_right = bbox[^2]
    x, y = int(top_left), int(top_left[^1])
    w = int(bottom_right) - x
    h = int(bottom_right[^1]) - y
```

EasyOCR возвращает полигональные bounding boxes — это удобно для текста под углом.[^17]

***

## Реализация прозрачного overlay-окна

Overlay — прозрачное окно поверх всех остальных, через которое можно кликать насквозь.[^18][^19]

```python
import tkinter as tk
import win32gui
import win32con

class TranslationOverlay:
    def __init__(self):
        self.root = tk.Tk()
        self.root.overrideredirect(True)      # без рамки и заголовка
        self.root.attributes('-topmost', True) # поверх всех окон
        self.root.attributes('-transparentcolor', 'black')  # чёрный = прозрачный
        self.root.config(bg='black')
        self.root.geometry(f'{screen_w}x{screen_h}+0+0')  # на весь экран
        
        # Сделать окно click-through (клики проходят сквозь него)
        hwnd = win32gui.FindWindow(None, self.root.title())
        styles = win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE)
        styles |= win32con.WS_EX_LAYERED | win32con.WS_EX_TRANSPARENT
        win32gui.SetWindowLong(hwnd, win32con.GWL_EXSTYLE, styles)
        
        self.canvas = tk.Canvas(
            self.root, 
            bg='black', 
            highlightthickness=0
        )
        self.canvas.pack(fill='both', expand=True)
    
    def show_translation(self, x, y, w, h, original_text, translated_text):
        """Отобразить переведённый текст поверх оригинала"""
        # Закрасить оригинальную область (белый прямоугольник)
        self.canvas.create_rectangle(
            x, y, x+w, y+h,
            fill='#FFFDE7',  # светло-жёлтый фон
            outline='#FFC107'
        )
        # Написать перевод
        self.canvas.create_text(
            x + w//2, y + h//2,
            text=translated_text,
            font=('Arial', max(9, h-4), 'bold'),
            fill='#1A237E',  # тёмно-синий текст
            width=w
        )
    
    def clear(self):
        self.canvas.delete('all')
```

Ключевой момент — `WS_EX_TRANSPARENT` из Win32 API делает окно «сквозным» для кликов мыши. Это значит пользователь может продолжать работать с приложением под переводом.[^19][^18]

***

## Захват области экрана и горячие клавиши

### Захват скриншота (mss — самый быстрый метод)

```python
import mss
import mss.tools
from PIL import Image

def capture_region(x, y, width, height):
    """Быстрый захват области экрана"""
    with mss.mss() as sct:
        monitor = {
            "top": y,
            "left": x, 
            "width": width,
            "height": height
        }
        screenshot = sct.grab(monitor)
        return Image.frombytes('RGB', screenshot.size, screenshot.bgra, 'raw', 'BGRX')
```

Библиотека `mss` использует нативные Windows API и значительно быстрее PIL/ImageGrab.[^20][^21]

### Выделение области мышью

```python
import tkinter as tk

class RegionSelector:
    def __init__(self, callback):
        self.callback = callback
        self.root = tk.Tk()
        self.root.attributes('-fullscreen', True)
        self.root.attributes('-alpha', 0.3)
        self.root.config(bg='grey')
        self.root.attributes('-topmost', True)
        
        self.canvas = tk.Canvas(self.root, cursor='cross', bg='grey')
        self.canvas.pack(fill='both', expand=True)
        
        self.start_x = self.start_y = 0
        self.rect = None
        
        self.canvas.bind('<ButtonPress-1>', self.on_press)
        self.canvas.bind('<B1-Motion>', self.on_drag)
        self.canvas.bind('<ButtonRelease-1>', self.on_release)
        self.root.bind('<Escape>', lambda e: self.root.destroy())
    
    def on_press(self, event):
        self.start_x, self.start_y = event.x, event.y
    
    def on_drag(self, event):
        if self.rect:
            self.canvas.delete(self.rect)
        self.rect = self.canvas.create_rectangle(
            self.start_x, self.start_y, event.x, event.y,
            outline='red', width=2, fill='white', stipple='gray25'
        )
    
    def on_release(self, event):
        x = min(self.start_x, event.x)
        y = min(self.start_y, event.y)
        w = abs(event.x - self.start_x)
        h = abs(event.y - self.start_y)
        self.root.destroy()
        if w > 5 and h > 5:
            self.callback(x, y, w, h)
```

### Горячие клавиши (фоновый listener)

```python
import keyboard

def setup_hotkeys(translate_callback, clear_callback):
    keyboard.add_hotkey('ctrl+shift+t', translate_callback)
    keyboard.add_hotkey('ctrl+shift+c', clear_callback)
    keyboard.add_hotkey('escape', clear_callback)
```

Библиотека `keyboard` работает глобально — перехватывает нажатия даже когда приложение в фоне.[^22]

***

## Перевод текста

### deep-translator (бесплатно, без API-ключа)

```python
from deep_translator import GoogleTranslator

def translate_text(text, source='en', target='ru'):
    """Перевод через Google Translate (бесплатно)"""
    try:
        translator = GoogleTranslator(source=source, target=target)
        result = translator.translate(text)
        return result
    except Exception as e:
        return f"[Ошибка перевода: {e}]"
```

`deep-translator` — бесплатная Python-библиотека, поддерживает Google Translate, DeepL (бесплатный tier), Microsoft Translator и другие без платных API-ключей.[^23][^24]

### Пакетный перевод слов (для UI-элементов)

Для интерфейсных слов важно **переводить каждое слово отдельно**, сохраняя его координаты:

```python
from deep_translator import GoogleTranslator

def translate_words_with_positions(word_boxes):
    """
    word_boxes: [(text, x, y, w, h), ...]
    Возвращает: [(translated, x, y, w, h), ...]
    """
    translator = GoogleTranslator(source='en', target='ru')
    result = []
    
    # Пакетный перевод для скорости
    texts = [wb for wb in word_boxes]
    try:
        # Соединяем через разделитель, переводим пакетом
        combined = ' ||| '.join(texts)
        translated_combined = translator.translate(combined)
        translations = translated_combined.split(' ||| ')
        
        for i, (orig, x, y, w, h) in enumerate(word_boxes):
            trans = translations[i] if i < len(translations) else orig
            result.append((trans, x, y, w, h))
    except:
        # Fallback: переводим по одному
        for (orig, x, y, w, h) in word_boxes:
            trans = translator.translate(orig)
            result.append((trans, x, y, w, h))
    
    return result
```

***

## Полная архитектура (итоговый поток)

```python
import threading
import keyboard
import tkinter as tk
from mss import mss
from PIL import Image
import cv2
import numpy as np
import pytesseract
from pytesseract import Output
from deep_translator import GoogleTranslator
import win32gui, win32con

# --- Главный поток ---
def on_hotkey():
    """Вызывается по Ctrl+Shift+T"""
    overlay.clear()
    selector = RegionSelector(callback=on_region_selected)
    selector.root.mainloop()

def on_region_selected(x, y, w, h):
    """Вызывается после выделения области"""
    # 1. Захват
    screenshot = capture_region(x, y, w, h)
    
    # 2. Предобработка
    processed = preprocess_for_ocr(screenshot)
    
    # 3. OCR — получение слов с координатами
    data = pytesseract.image_to_data(
        processed,
        config='--oem 3 --psm 11',
        output_type=Output.DICT
    )
    
    word_boxes = []
    for i in range(len(data['text'])):
        if int(data['conf'][i]) > 30 and data['text'][i].strip():
            # Пересчёт координат относительно экрана
            wx = x + data['left'][i] // 3   # делим на 3 из-за апскейла
            wy = y + data['top'][i] // 3
            ww = data['width'][i] // 3
            wh = data['height'][i] // 3
            word_boxes.append((data['text'][i], wx, wy, ww, wh))
    
    if not word_boxes:
        return
    
    # 4. Перевод
    translated_boxes = translate_words_with_positions(word_boxes)
    
    # 5. Отображение overlay
    for (trans_text, tx, ty, tw, th) in translated_boxes:
        overlay.show_translation(tx, ty, tw, th, '', trans_text)

# Запуск
overlay = TranslationOverlay()
keyboard.add_hotkey('ctrl+shift+t', lambda: threading.Thread(target=on_hotkey).start())
overlay.root.mainloop()
```

***

## Реальные проекты для изучения и вдохновения

Существует несколько открытых проектов, реализующих похожую функциональность. CODX GPT сможет изучить их исходники:

| Проект | Язык | OCR | Особенности | Ссылка |
|---|---|---|---|---|
| **Translumo** | C# | Windows OCR, EasyOCR, Tesseract | 4.2k ⭐, комбинирует несколько OCR + ML-ранжирование[^13] | github.com/ramjke/Translumo |
| **OverText** | Python | EasyOCR | Real-time overlay, auto-update, deep-translator[^4] | github.com/SiENcE/overtext |
| **OCRTranslator** | Python | Tesseract | Hotkey, rectangle selection, Google+AI перевод[^25] | github.com/keegang6705/OCRTranslator |
| **pyugt** | Python | Tesseract v5 | Cross-platform, DeepL support, preprocessing[^26][^27] | github.com/lrq3000/pyugt |
| **game-overlay-translator** | Python | Tesseract | Real-time, tesseract+googletrans[^28] | github.com/ismailnajah/game-overlay-translator |
| **Lingo-Live** | Python | Tesseract | CustomTkinter UI, стильный дизайн[^29] | dev.to/samar_shetye |
| **narzau/translator** | Python | — | Ctrl+Alt+X hotkey, draggable overlay[^30] | github.com/narzau/translator |

**Самое важное для изучения — Translumo**: его рекомендации однозначны — Windows OCR (WinRT) превосходит Tesseract и EasyOCR для этой задачи по скорости и точности.[^13]

***

## Использование Windows OCR (WinRT) через Python

Windows встроенный OCR через WinRT API — самый точный вариант для экранного текста на Windows 10/11:[^31][^32]

```python
# pip install winrt-ocr-python
import asyncio
from winrt.windows.media.ocr import OcrEngine
from winrt.windows.globalization import Language
from winrt.windows.graphics.imaging import SoftwareBitmap, BitmapDecoder

async def windows_ocr(image_path):
    lang = Language("en-US")
    engine = OcrEngine.try_create_from_language(lang)
    
    # Загрузка изображения в SoftwareBitmap
    # ... (см. документацию winrt-ocr-python)
    
    result = await engine.recognize_async(bitmap)
    words_with_boxes = []
    
    for line in result.lines:
        for word in line.words:
            bounds = word.bounding_rect
            words_with_boxes.append({
                'text': word.text,
                'x': bounds.x,
                'y': bounds.y,
                'w': bounds.width,
                'h': bounds.height
            })
    
    return words_with_boxes
```

Windows OCR возвращает структуру Lines → Words с bounding rect для каждого слова — это именно то, что нужно для точного overlay.[^33]

***

## Специфические техники для UI-интерфейсов

### Проблема: текст на цветных кнопках

UI-кнопки часто имеют тёмный текст на цветном фоне. Стандартная бинаризация ломает распознавание. Решение — инвертированная бинаризация + цветовая нормализация:

```python
def preprocess_ui_button(img):
    """Специальная обработка для UI-кнопок"""
    # Нормализация яркости
    lab = cv2.cvtColor(img, cv2.COLOR_BGR2LAB)
    l, a, b = cv2.split(lab)
    clahe = cv2.createCLAHE(2.0, (8,8))
    l = clahe.apply(l)
    img = cv2.merge([l, a, b])
    img = cv2.cvtColor(img, cv2.COLOR_LAB2BGR)
    
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    
    # Попробовать обе версии (прямую и инвертированную)
    # и взять ту, что даёт больше текста
    _, thresh_normal = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    _, thresh_inverted = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY_INV + cv2.THRESH_OTSU)
    
    return thresh_normal, thresh_inverted
```

### Проблема: мелкий текст

Для текста менее 15px — апскейл с использованием Lanczos/bicubic интерполяции перед OCR:[^34][^7]

```python
def upscale_for_ocr(image, min_height_px=40):
    """Апскейл если текст слишком мелкий"""
    h = image.height
    if h < min_height_px:
        scale = min_height_px / h
        new_w = int(image.width * scale)
        new_h = int(h * scale)
        image = image.resize((new_w, new_h), Image.LANCZOS)
    return image
```

### Стратегия двойного прохода OCR

Для максимальной точности — запускать OCR дважды с разными настройками и объединять результаты:

```python
def dual_pass_ocr(image):
    """Два прохода OCR с разными PSM"""
    img = preprocess_for_ocr(image)
    
    # Проход 1: sparse text (для UI)
    result1 = pytesseract.image_to_data(img, config='--oem 3 --psm 11', output_type=Output.DICT)
    
    # Проход 2: auto page segmentation (для блоков текста)
    result2 = pytesseract.image_to_data(img, config='--oem 3 --psm 3', output_type=Output.DICT)
    
    # Объединить уникальные слова (по позиции)
    # ...
    return combined_results
```

***

## Список зависимостей (requirements.txt)

```
# Скриншоты
mss>=9.0.0
Pillow>=10.0.0

# OCR
pytesseract>=0.3.10
easyocr>=1.7.0

# Предобработка изображений
opencv-python>=4.8.0
numpy>=1.24.0

# Перевод
deep-translator>=1.11.0

# Горячие клавиши
keyboard>=0.13.5

# Windows API (для click-through overlay)
pywin32>=306

# Опционально: Windows OCR
winrt-ocr-python>=0.1.0
```

**Tesseract** нужно установить отдельно: https://github.com/UB-Mannheim/tesseract/wiki — установить с пакетом языков `eng`.[^35][^26]

***

## Сравнение подходов: простой vs продвинутый

| Подход | Сложность | Качество для UI | Требования |
|---|---|---|---|
| Tesseract PSM 3 (текущий) | Низкая | ❌ Плохо | Только Tesseract |
| Tesseract PSM 11 + preprocessing | Средняя | ✅ Хорошо | OpenCV + предобработка[^1][^9] |
| EasyOCR + preprocessing | Средняя | ✅ Хорошо | GPU опционально[^12] |
| Windows OCR (WinRT) | Средняя | ✅✅ Отлично | Windows 10+ 1803[^32] |
| PaddleOCR | Высокая | ✅✅ Отлично | Большая зависимость[^36] |
| GPT-4o Vision API | Низкая (код) | ✅✅✅ Максимум | Платный API[^14] |

***

## Рекомендуемый стек для CODX GPT

На основе исследования — **оптимальный стек**:

1. **Hotkey**: `keyboard` библиотека — `Ctrl+Shift+T`
2. **Region Select**: полупрозрачный `tkinter` поверх экрана с выделением мышью
3. **Screenshot**: `mss` — самый быстрый вариант[^21]
4. **Preprocessing**: `OpenCV` — апскейл 3×, CLAHE, адаптивная бинаризация, белая рамка 20px
5. **OCR**: `EasyOCR` для первого варианта (проще) ИЛИ `Windows OCR (WinRT)` для лучшего качества
6. **Translation**: `deep-translator` с Google Translate backend (бесплатно)[^23]
7. **Overlay**: `tkinter` + `pywin32` для click-through, фоновый цвет = прозрачный ключ[^18]
8. **Per-word positioning**: `image_to_data()` (Tesseract) или EasyOCR bbox[^15]

Этот стек полностью соответствует архитектуре проекта **OverText** (github.com/SiENcE/overtext) и принципам **Lingo-Live**, которые были реализованы именно под эту задачу.[^29][^4]

---

## References

1. [Tesseract Page Segmentation Modes (PSMs) Explained](https://pyimagesearch.com/2021/11/15/tesseract-page-segmentation-modes-psms-explained-how-to-improve-your-ocr-accuracy/) - In the first part of this tutorial, we'll discuss what page segmentation modes (PSMs) are, why they ...

2. [Experimenting Tesseract's Page Segmentation Modes (PSM) on ...](https://ai.gopubby.com/experimenting-tesseracts-page-segmentation-modes-psm-on-various-images-a6056ef9c7b3) - The Page Segmentation Mode (PSM) dictates how Tesseract interprets the structure of the input image,...

3. [Text Cleaning | Help Center](https://docs.easyrpa.eu/help/docs/Data-Analyst-Guide/OCR-Tuning-Guide/Common-OCR-Challenges-and-their-Resolution/Text-Cleaning) - Cleaning a text image for OCR is the process of applying preprocessing techniques to enhance the ima...

4. [GitHub - SiENcE/overtext: A real-time screen translation overlay tool that captures text from your screen, translates it, and displays the translation directly over the original content.](https://github.com/SiENcE/overtext) - A real-time screen translation overlay tool that captures text from your screen, translates it, and ...

5. [Page Segmentation Modes | tesseract-ocr/tessdoc | DeepWiki](https://deepwiki.com/tesseract-ocr/tessdoc/6.2-page-segmentation-modes) - This page explains Tesseract's page segmentation modes (PSM), which control how Tesseract analyzes t...

6. [Improve Accuracy by tuning PSM values of Tesseract - Part 2](https://www.cloudthat.com/resources/blog/improve-accuracy-by-tuning-psm-values-of-tesseract-part-2) - This blog continues with Part 1 of Improve Accuracy by tuning the PSM values of Tesseract, where I h...

7. [How to Improve OCR Accuracy using Image Preprocessing - DEV IT](https://devitpl.com/application-development/how-to-improve-ocr-accuracy-using-image-preprocessing/) - 2. Binarization. Binarization means converting a colored image into an image that consists of only b...

8. [Improving OCR Results with Basic Image Processing](https://pyimagesearch.com/2021/11/22/improving-ocr-results-with-basic-image-processing/) - Learn how basic image processing can dramatically improve the accuracy of Tesseract OCR; Discover ho...

9. [How to make an image to text classifier application](https://www.modb.pro/db/1686255181871783936) - How to improve accuracy of tesseractThe default Page segmentation method (psm) in tesseract is page

10. [Borders](https://tesseract-ocr.github.io/tessdoc/ImproveQuality.html) - Tesseract documentation

11. [Question: Tesseract OCR sometimes reads text wrong – any tips ...](https://github.com/orgs/community/discussions/180418) - Convert all images to 300 DPI before OCR (low DPI causes misreads). · Preprocess images: grayscale →...

12. [Evaluating OCR Performance for Assistive Technology - arXiv](https://arxiv.org/html/2602.02223v1)

13. [GitHub - ramjke/Translumo: Advanced real-time screen translator for ...](https://github.com/ramjke/Translumo) - Advanced real-time screen translator for games, hardcoded subtitles in videos, static text and etc. ...

14. [Best OCR Tools for AI Agents - Top Vision APIs 2026 | Fast.io](https://fast.io/resources/best-ocr-tools-ai-agents/) - Compare the top OCR tools and vision APIs for AI agents. From GPT-4o to Azure Document AI, find the ...

15. [Getting the bounding box of the recognized words using python ...](https://stackoverflow.com/questions/20831612/getting-the-bounding-box-of-the-recognized-words-using-python-tesseract) - The bounding boxes returned by pytesseract.image_to_boxes() enclose letters so I believe pytesseract...

16. [Python OCR Tutorial: Tesseract, Pytesseract, and OpenCV - Nanonets](https://nanonets.com/blog/ocr-with-tesseract/) - The script below will give you bounding box information for each character detected by tesseract dur...

17. [boysugi20/python-image-translator - GitHub](https://github.com/boysugi20/python-image-translator) - Image Translator: OCR-based tool for translating text within images using Google Translate. Effortle...

18. [Tying to set non-interactable (click-through) overlay with TkInter](https://stackoverflow.com/questions/61114145/tying-to-set-non-interactable-click-through-overlay-with-tkinter) - You can use root.attributes('-transparentcolor', 'white', '-topmost', 1) and root.config(bg='white')...

19. [Tkinter see through window not affected by mouse clicks](https://stackoverflow.com/questions/29458775/tkinter-see-through-window-not-affected-by-mouse-clicks) - I am currently controlling a game with python by sending mouse and keystroke commands. What I am loo...

20. [Examples](https://python-mss.readthedocs.io/examples.html)

21. [Python MSS](https://pypi.org/project/mss/) - An ultra fast cross-platform multiple screenshots module in pure python using ctypes.

22. [We write a simple translator of texts from pictures and ...](https://blog.a7in.com/we-write-a-simple-translator-of-texts-from-pictures-and-screenshots-in-python/?lang=en) - If you like to code and play a little bit more text games that are often not in your language, then ...

23. [deep-translator¶](https://deep-translator.readthedocs.io/en/latest/README.html)

24. [nidhaloff/deep-translator](https://github.com/nidhaloff/deep-translator) - A flexible free and unlimited python tool to translate between different languages in a simple way u...

25. [keegang6705/OCRTranslator - GitHub](https://github.com/keegang6705/OCRTranslator) - Trigger: Press the hotkey ( Scroll Lock by default) to open the selection UI. Select: Draw a rectang...

26. [pyugt - Python Universal Game Translator - PyPI](https://pypi.org/project/pyugt/) - pyugt is a universal game translator coded in Python: it takes screenshots from a region you select ...

27. [pyugt - Python Universal Game Translator - GitHub](https://github.com/lrq3000/pyugt) - pyugt is a universal game translator coded in Python, translating games from screenshots, it can ful...

28. [ismailnajah/game-overlay-translator: A program that uses ... - GitHub](https://github.com/ismailnajah/game-overlay-translator) - Game-overlay Translator. A program that uses the tesseract-ocr + googletrans API to translate any im...

29. [Stop Copy-Pasting from Images: Build a Universal Screen Translator with Python](https://dev.to/samar_shetye/stop-copy-pasting-from-images-build-a-universal-screen-translator-with-python-54gm) - Lingo-Live started with a frustration I’m sure you’ve felt too. Have you ever tried copying text...

30. [GitHub - narzau/translator: Translator Overlay](https://github.com/narzau/translator) - Translator Overlay. Contribute to narzau/translator development by creating an account on GitHub.

31. [Get Started with Text Recognition (OCR) in the Windows App SDK](https://learn.microsoft.com/en-us/windows/ai/apis/text-recognition) - Learn about the Artificial Intelligence (AI) text recognition features that ship with the Windows Ap...

32. [winrt-ocr-python](https://pypi.org/project/winrt-ocr-python/) - Python wrapper for OCR by Windows Runtime API

33. [winrt-api/windows.media.ocr/ocrengine.md at docs · MicrosoftDocs/winrt-api](https://github.com/MicrosoftDocs/winrt-api/blob/docs/windows.media.ocr/ocrengine.md) - WinRT reference content for developing Microsoft Universal Windows Platform (UWP) apps - MicrosoftDo...

34. [Preprocessing Images to Improve OCR & DarkShield Results - IRI](https://www.iri.com/blog/data-protection/preprocessing-images-for-ocr-darkshield/) - Optical Character Recognition (OCR) software is technology that recognizes text within a digital ima...

35. [I Tried Creating My Own OCR Translator Tool Using Python ... - Reddit](https://www.reddit.com/r/visualnovels/comments/pfz1vk/i_tried_creating_my_own_ocr_translator_tool_using/) - I want to share an app that I made using python, I call it screen translate. The looks and method ar...

36. [PaddlePaddle/PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) - Turn any PDF or image document into structured data for your AI. A powerful, lightweight OCR toolkit...

