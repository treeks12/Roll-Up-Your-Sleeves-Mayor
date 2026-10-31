# SC4Evolution — SimCity 4-inspired mod for Cities: Skylines

[Português](#português) · [English](#english)

## Português

SC4Evolution é um mod experimental para **Cities: Skylines 1** inspirado na experiência de administrar uma cidade em **SimCity 4**. A ideia é combinar a construção e o mapa amplo do CS1 com assessores, informações municipais e decisões de desenvolvimento que lembrem o SC4. É um projeto de fãs independente, não afiliado às empresas dos jogos.

**Estado: beta experimental, em desenvolvimento.** Não recria integralmente a simulação do SC4. A interface e os textos dos assessores estão disponíveis em português brasileiro e inglês, conforme o idioma do jogo. Consulte [idioma e limitações](docs/LOCALIZATION.md).

### O que já existe

- Sete assessores integrados à interface, com retratos e avisos baseados em indicadores da cidade; episódios são acompanhados e não devem ser repetidos a cada atualização. Educação e saúde consideram o desbloqueio dos serviços.
- Protótipos de classes residenciais, demanda e admissão de construção, incluindo política local de água. São mudanças experimentais, dependentes da configuração e de compatibilidade com a versão do jogo.
- Financiamento cívico experimental por equipamento, com suporte limitado a escolas e equipamentos de saúde mapeados.
- Modelos de emprego, bairros e decisões; sinais estimados de tributação e acesso são diagnósticos, não substituem a arrecadação nem o cálculo de rotas do jogo. Transporte de cargas ainda não dispõe das observações necessárias.
- Animações dos assessores podem ser preparadas localmente a partir dos arquivos da instalação de SC4. A experiência completa e o balanceamento ainda exigem testes jogando.

Os recursos originais dos jogos, executáveis, saves e builds locais não são distribuídos aqui. Os nomes e jogos pertencem aos respectivos titulares.

### Preparação para desenvolvimento (Windows)

Requer instalações próprias de Cities: Skylines 1 e SimCity 4, PowerShell 7, o SDK .NET indicado em `global.json` e internet para preparar dependências. Copie `config/paths.example.json` para `config/local.paths.json` e configure os caminhos locais. Na raiz do projeto:

```powershell
pwsh -File scripts/Bootstrap.ps1
pwsh -File scripts/Prepare-LocalData.ps1
pwsh -File scripts/Build.ps1
pwsh -File scripts/Test.ps1
pwsh -File scripts/Install-LocalMod.ps1
```

Ative SC4Evolution em Gerenciador de Conteúdo > Mods. O mod utiliza Cities Harmony; `Bootstrap.ps1` prepara as dependências e `scripts/Install-HarmonyDependency.ps1` oferece instalação local do provider quando necessário. Leia os scripts antes de executá-los. `config/economic-bridge.shadow-opt-in.example` documenta um preset experimental; a configuração local efetiva é `config/economic-bridge.config`. Use uma cidade de teste e faça backup de seus saves. Este repositório é código-fonte, não uma distribuição pronta do Workshop.

## English

SC4Evolution is an experimental **Cities: Skylines 1** mod inspired by the city-management experience of **SimCity 4**. It aims to combine CS1's building tools and larger maps with SC4-like advisors, city information and development decisions. This is an independent fan project, unaffiliated with the games' companies.

**Status: experimental beta, work in progress.** It does not yet reproduce the complete SC4 simulation. The interface and advisor messages support English and Brazilian Portuguese, following the game language. See [language behavior and limitations](docs/LOCALIZATION.md).

### Current features and limits

- Seven advisors integrated into the native UI, with portraits and city-indicator alerts. Issue episodes are tracked to avoid repeating an alert at every update; education and healthcare alerts consider service unlocks.
- Experimental residential classes, demand and building admission, including a local water policy. Effects depend on configuration and game-version compatibility.
- Experimental facility-level civic funding, limited to mapped schools and healthcare facilities.
- Employment, neighborhood and decision models. Estimated tax and access signals are diagnostic; they do not replace the game's tax revenue or routing. Freight lacks the observations it needs.
- Advisor animations can be generated locally from the user's own SC4 installation. The overall experience and balance still need gameplay testing.

Original game assets, executables, saves and local builds are not distributed in this repository. Game names and assets belong to their respective owners.

### Development setup (Windows)

Requires your own installations of Cities: Skylines 1 and SimCity 4, PowerShell 7, the .NET SDK specified in `global.json`, and internet access to prepare dependencies. Copy `config/paths.example.json` to `config/local.paths.json` and set your local game paths. From the project root:

```powershell
pwsh -File scripts/Bootstrap.ps1
pwsh -File scripts/Prepare-LocalData.ps1
pwsh -File scripts/Build.ps1
pwsh -File scripts/Test.ps1
pwsh -File scripts/Install-LocalMod.ps1
```

Enable SC4Evolution in Content Manager > Mods. The mod uses Cities Harmony; `Bootstrap.ps1` prepares dependencies, and `scripts/Install-HarmonyDependency.ps1` can install the local provider if needed. Read the scripts before running them. `config/economic-bridge.shadow-opt-in.example` documents an experimental preset; the effective local configuration is `config/economic-bridge.config`. Use a test city and back up saves. This is a source repository, not a ready-to-install Workshop release.
