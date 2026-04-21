# Run Jargon

Run Jargon is a Windows desktop app for translating text directly from screen captures.

## Stack

- WPF / .NET on Windows
- Windows OCR
- Local offline translation through Argos Translate
- Optional Azure Translator
- OpenCV-based background cleanup and overlay rendering

## What It Does

1. Captures a selected screen region
2. Detects and recognizes text
3. Translates the recognized content
4. Draws the translation back over the original image as an overlay

## Current Focus

The current architecture is being shaped around layout-aware OCR and translation so that the app works not only for paragraphs, but also for dense UI surfaces such as tabs, menus, table headers, and short labels.

## Solution

- Solution: `RunJargon.slnx`
- App project: `RunJargon.App/RunJargon.App.csproj`
- Tests: `RunJargon.App.Tests/RunJargon.App.Tests.csproj`

## Build

```powershell
dotnet build .\RunJargon.slnx
dotnet test .\RunJargon.slnx
```
