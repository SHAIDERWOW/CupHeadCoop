# CupheadCoop — Mod de Co-op Online Nativo + Sincronização de Conquistas Steam

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Plataforma-Steam%20(PC)-orange.svg)](https://store.steampowered.com/app/268910/Cuphead/)
[![Versão do Jogo](https://img.shields.io/badge/Cuphead-v1.3.4%20%2B%20DLC-brightgreen.svg)]()
[![Releases](https://img.shields.io/github/v/release/SHAIDERWOW/CupHeadCoop?color=red)](https://github.com/SHAIDERWOW/CupHeadCoop/releases)

**Português (Brasil)** | [**English**](README.md)

> **Mod de multiplayer online nativo e 100% clean-room para Cuphead (versão Steam).**  
> Jogue a campanha principal completa e a DLC *The Delicious Last Course* com um amigo pela internet sem o atraso do Steam Remote Play Together, sem necessidade de Hamachi ou Radmin, e com **conquistas da Steam e progresso do save sincronizados para AMBOS os jogadores**!

---

## 🎮 Funcionalidades Principais

- **🏆 Sincronização Completa de Conquistas Steam (`CoopOnlineInterface`):**
  - Todas as 42 Conquistas Steam (jogo base + DLC) desbloqueiam para o **Host e para o Cliente** em tempo real.
  - Venceu um chefe jogando de Ms. Chalice, comprou itens no Empório do Porkrind ou derrotou chefes secretos? As conquistas sobem na hora na conta Steam dos dois jogadores!
- **⚡ Zero Atraso de Input (Controle 100% Local):**
  - Diferente do Steam Remote Play Together (que transmite vídeo comprimido com lag nos botões), os dois jogadores rodam o Cuphead localmente no próprio PC. Seus pulos, parries, dashes e tiros respondem na hora (0ms de atraso).
- **🌐 Steam P2P Integrado (Sem Redirecionar Portas / Sem VPN):**
  - Conecte-se facilmente através da sua lista de amigos da Steam ou compartilhando um Código de Sala com atalhos de teclado (`F5` / `F6`). Sem Hamachi, sem Radmin e sem abrir portas no roteador.
  - Também inclui modo Direto por UDP para quem preferir jogar em LAN / rede local offline.
- **🔄 Sincronização Completa da Gameplay:**
  - **Mapa-Múndi:** Movimentação, direções, animações de caminhada e desejo de vida dobrada do Djimmi sincronizados.
  - **Chefes e Linha do Tempo:** Vida do chefe sincronizada (`Level.Timeline.damage`), mudanças de fase sincronizadas e números aleatórios determinísticos por semente (RNG).
  - **Fases Run & Gun:** Moedas (`LevelCoin`) coletadas por qualquer um dos jogadores são computadas para ambos (`5/5`).
  - **Cassino do Rei Dado:** Posição no tabuleiro, casas com corações extras (`HEART_INDEXES`) e o resultado do parry no dado (`DicePalaceMainLevelDice`) sincronizados com precisão.
  - **Mecânicas de Reviver:** Tanto o parry no coração rosa do fantasma (`OnParrySwitch`) quanto o roubo de 1 HP do parceiro no topo da tela (`ReviveOutOfFrame`) funcionam perfeitamente no online.
  - **Fases de Avião (Shmup):** Disparos, bombas, ataques EX, Supers, parries e o estado de encolher em mini-avião (`Shrink_In` / `Shrink_Out`) totalmente sincronizados.
  - **Troca de Equipamentos em Tempo Real:** Trocar de armas ou amuletos (ex.: Biscoito Astral da Ms. Chalice, Smoke Bomb, Relíquia Amaldiçoada) no Mapa ou na tela de Game Over atualiza imediatamente para o outro jogador.
- **🛡️ Proteção Contra Travamentos de Menus (Anti-Softlock):**
  - Corrige travamentos nativos do jogo ao fechar a tela de equipamentos (`MapEquipUI`) ou ao sair da loja do Porkrind (`ShopScene`).
  - Bloqueia cliques acidentais na opção "Remover Jogador 2" no menu de Pause durante a partida online.
  - Sincronização rápida de *Retry/Restart* imediato na tela de Game Over e de Pause.
- **🧹 Limpo e 100% Reversível:**
  - Carregado via Unity Doorstop. **Nunca altera nem substitui os arquivos originais do seu Cuphead**.
  - Desinstalação simples e limpa: basta apagar os 3 arquivos do mod para voltar ao jogo 100% vanilla a qualquer momento.

---

## 📥 Como Instalar

> **Observação:** Você e seu amigo precisam ter o jogo Cuphead original na Steam.

1. Baixe o arquivo `CupheadCoop-v1.0.zip` na página de [Releases](https://github.com/SHAIDERWOW/CupHeadCoop/releases).
2. Extraia o conteúdo do arquivo `.zip` diretamente dentro da pasta de instalação do seu Cuphead:
   - Caminho padrão da Steam: `C:\Program Files (x86)\Steam\steamapps\common\Cuphead\`
3. Verifique se os arquivos abaixo estão na pasta junto com o `Cuphead.exe`:
   - `winhttp.dll`
   - `doorstop_config.ini`
   - `CupheadCoop.dll`
4. Inicie o Cuphead normalmente pela Steam!

---

## 🕹️ Como Jogar Online

1. Abra o Cuphead na Steam (ambos os jogadores).
2. Pressione **`F9`** na Tela de Título ou no Mapa para abrir o menu do **CupheadCoop**:
   - O menu mostra o status da Steam API, ping, seu papel (Host/Client) e as opções de sala.

### Opção A: Sala de Amigos da Steam (Recomendado — Mais Fácil)
- **Host (Quem vai hospedar):** Pressione **`F5`** (ou clique em **"1. CRIAR SALA STEAM - Host"**). Em seguida, pressione **`F6`** para copiar o código da sala e mande para seu amigo (ou clique em **"Convidar na Steam"** para abrir o menu de amigos da Steam).
- **Client (Quem vai entrar):** Copie o código de sala que o Host lhe enviou, pressione **`F6`** (ou clique em **"Colar+Entrar"**) dentro do menu `F9`.
- **Pronto!** Carregue qualquer slot de save. O Jogador 2 entrará automaticamente no jogo como Mugman ou Ms. Chalice!

### Opção B: Direto por UDP (Rede Local / Offline)
- O Host escolhe uma porta (padrão: `26891`) e clica em **Host UDP**.
- O Client digita o IP local do Host e clica em **Join UDP**.

---

## 🗑️ Como Desinstalar (Voltar ao Jogo Original)

Se você ou seu amigo quiserem remover o mod:
1. Abra a pasta do seu Cuphead e simplesmente delete os 3 arquivos do mod:
   - `winhttp.dll`
   - `doorstop_config.ini`
   - `CupheadCoop.dll`
2. Pronto! O jogo voltará a ser 100% original sem alterar seus saves nem a DLC.

---

## ⌨️ Teclas de Atalho

| Tecla | Ação |
| :---: | :--- |
| **`F9`** | Abre/fecha o Menu do Mod e o cursor do mouse |
| **`F5`** | Cria a Sala Steam (se estiver desconectado) / Abre o convite de amigos da Steam (se for Host) |
| **`F6`** | Cola o código da sala da área de transferência e entra / Copia o código da sala atual (se for Host) |

---

## 🧪 Testes Automatizados de Validação (QA)

O projeto conta com uma suíte de testes automatizados ([`tests/run_automated_qa.py`](tests/run_automated_qa.py)) que inicializa uma instância isolada do jogo em sandbox e valida 7 etapas fundamentais de rede:
1. Inicialização do Doorstop e comunicação com a ponte RPC interna
2. Handshake de rede e ativação do modo multiplayer para 2 jogadores
3. Interceptação local de conquista Steam
4. Sincronização remota da conquista no outro jogador
5. Spawn dos dois jogadores na cena do chefe (`scene_level_veggies`)
6. Sincronização contínua de posição e tiro em tempo real
7. Verificação de subsistemas críticos (anti-softlocks, troca de armas, espelhamento de loja e pacotes do Rei Dado)

Para rodar os testes:
```powershell
python tests/run_automated_qa.py
```

---

## 📜 Licença e Avisos

- Distribuído sob a licença [MIT License](LICENSE).
- Utiliza o carregador [Unity Doorstop](https://github.com/NeighbourhoodRevival/UnityDoorstop).
- Cuphead é uma marca registrada da **Studio MDHR Entertainment Inc.** Este é um projeto de código aberto independente, sem qualquer afiliação oficial com a Studio MDHR.
