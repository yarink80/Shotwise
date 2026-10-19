# Contributing to Shotwise

Thanks for helping! Bug reports, ideas, translations and pull requests are all welcome.

## Before you start

- **Bugs and ideas:** open an issue with the [bug report](https://github.com/yarink80/Shotwise/issues/new?template=bug_report.yml)
  or [feature request](https://github.com/yarink80/Shotwise/issues/new?template=feature_request.yml) form. Search first; a
  thumbs-up on an existing issue helps prioritise.
- **Security problems:** do not open an issue. See [SECURITY.md](SECURITY.md).
- **Bigger changes** (new capture modes, new share destinations, new dependencies): open an issue first so we can agree on the
  approach before you invest time.

## Build and test

You need Windows 10 2004+ (Windows 11 recommended), the .NET 8 SDK and, for the Explorer shell extension, the Visual Studio 2022
Build Tools with the C++ workload.

```powershell
powershell -File tools/build.ps1 -Test        # build everything + native shell extension + all tests
powershell -File tools/run.ps1 -Isolated      # run with a throw-away data root (your real profile is untouched)
```

`docs/SMOKE-TEST.md` has the manual checklist and the automated `--smoke` run for changes that touch capture, recording or
the editor UI.

## Pull requests

- Keep each PR focused on one change, and describe what it does and how you tested it (screenshots for UI changes).
- Add or update unit tests for logic in Core, Editor, Rendering, Vision, Media, Library, Sharing and Platform.
- Follow the existing layering: `Shotwise.Core` holds the model and the service interfaces; the engine libraries talk to each
  other only through those interfaces; UI libraries never reference engine libraries; `Shotwise.App` composes everything.
- New NuGet packages go into `Directory.Packages.props` (central package management) with a short comment, and their licence
  into `THIRD-PARTY-NOTICES.md`. Only permissive licences (MIT, BSD, Apache-2.0 and similar) can be accepted.
- All artwork (icons, stamps, templates, sounds) must be original or permissively licensed with attribution. Do not copy
  assets, text or code from other screen-capture products.
- User-facing strings go into the `.resx` resources so they can be translated.

## Translations

Shotwise ships in English. Resource stubs exist for German, Spanish, French, Japanese and Brazilian Portuguese; translated
`.resx` files are very welcome.

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
