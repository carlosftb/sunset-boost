# Changelog

As mudanças de cada versão do Sunset Boost. O workflow de release usa a seção da versão como notas da página de download.

## [0.2.0] - Beta

### Novo
- **Verificador de hardware** (aba Diagnóstico): procura configurações do PC que roubam desempenho e explica como corrigir. Só lê, não altera nada.
  - Monitor abaixo do Hz máximo, RAM sem XMP/EXPO, com um pente só ou com pentes diferentes.
  - Driver de vídeo com mais de 6 meses, PC com duas placas, notebook na bateria, disco do Windows cheio.
  - "Otimizações para jogos em janela" desligada e aceleração do mouse ligada.
  - Limite de FPS sugerido pelo Hz da tela e dicas de driver conforme a marca da placa.
- **Placa de vídeo dedicada por jogo** (aba Perfis): marca o jogo como "Alto desempenho" na configuração do próprio Windows. Valorant, LoL e Fortnite são encontrados sozinhos; o botão **+ JOGO** adiciona outros.
- Comando `sunsetboost-cli diagnostico`.

### Mudou
- Jogo e anti-cheat mostram a RAM como "—": o app não abre esses processos nem pra medir.
- Ícone do Atalho alinhado com os outros cards.

## [0.1.0] - Beta

Primeira versão pública.

### Novo
- **Início**: RAM, CPU e GPU em tempo real, quanto dá pra liberar e quem está comendo a memória.
- **Limpeza**: fecha navegadores, launchers e apps parados. Você escolhe o que fecha; jogos, anti-cheats e o Windows nunca fecham.
- **Modo Jogo**: limpa a memória em cache, pausa Search, SysMain e Spooler, ativa o plano de desempenho, desliga a gravação do Xbox e as notificações. Tudo é salvo antes e restaurado depois.
- **Perfis**: Valorant, League of Legends, Steam, Epic e Sem perfil, com troca automática ao abrir o jogo.
- **Atalho global** <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>B</kbd> e opção de abrir junto com o Windows.
- **Diagnóstico** com ajustes recomendados pro PC.
- Instalador para Windows 10 (2004+) e 11, 64 bits, com o .NET embutido.

### Acessibilidade
- Todos os controles funcionam pelo teclado (<kbd>Tab</kbd> e <kbd>Espaço</kbd>), com contorno de foco visível.
- Botões e interruptores com nome pro leitor de tela.

### Conhecido
- O instalador não tem assinatura digital: o SmartScreen avisa na primeira vez (veja o README).
- Ainda falta o teste longo em partidas reais.
