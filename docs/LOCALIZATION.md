# Idioma / Language

O mod usa o idioma selecionado no Cities: Skylines: português para códigos de idioma que começam com `pt`; inglês para todos os demais. Reinicie a cidade depois de trocar o idioma para reconstruir os controles e avisos já abertos. Textos históricos de notícias armazenados antes da troca permanecem no idioma em que foram gerados. Nomes dos assessores são preservados. A disponibilidade de espaço e a aparência da tradução na interface nativa ainda dependem de teste visual no jogo.

The mod follows the language selected in Cities: Skylines: Portuguese for locale codes beginning with `pt`, English for all others. Reload the city after changing languages to rebuild already-open controls and notices. Historical news stored before a language change remains in its original language. Advisor names are retained. The layout and visual fit of translated text in the native UI still need in-game visual testing.

## Source setup verification

On 2026-10-30, the public source checkout was prepared against local Steam installations using `Bootstrap.ps1` and `Prepare-LocalData.ps1`. The mod then compiled with `Build.ps1 -Destination Preview` (zero warnings or errors), and all 24 console-check projects passed with `Test.ps1`. This verifies source preparation and automated checks on that Windows setup; it does not verify the visual layout in a running game. Native visual testing was attempted separately but Cities: Skylines exited before loading with `PlatformServiceNotRunningException`.
