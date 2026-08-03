# BSGO Farm Bot
MİLLET BABA DİYİP GEÇİNEMİYO AQ
A man-in-the-middle farm bot for a **private** Battlestar Galactica Online server. It sits
between `bsgo.exe` and the server, watches the traffic to build a live picture of the sector, and
injects game commands so the bot can fight and mine while the real client stays open and renders
normally.

```
bsgo.exe ──► bsgobot (127.0.0.1:27050) ──► your server
   ▲                    │
   │ renders normally   │ injects LockTarget / Cast / Toggle / Mining / Loot / steering
   └────────────────────┘
```

Frames are forwarded **byte-for-byte unmodified**, so the client's own login, catalogue and chat
are never disturbed. The bot only observes and adds.

## Where to start

| | |
|---|---|
| [`bot/README.md`](bot/README.md) | What it does, how to run it, what every panel and setting means |
| [`docs/BOT.md`](docs/BOT.md) | How the bot decides things, and why each rule is the way it is |
| [`docs/GAME.md`](docs/GAME.md) | Game and protocol facts, checked against the client and server sources |

Requires .NET 9 and Windows. `dotnet build bot/BsgoBot` and run the exe.

## What's not here

`bot.json` is gitignored — it holds a session token, which is a credential. The app writes one on
first run; [`bot/BsgoBot/bot.example.json`](bot/BsgoBot/bot.example.json) shows the shape.

The decompiled client sources, the server implementations and the launcher payload the project
was developed against are not included. None of that is this project's code, and the bot reads
nothing from it at runtime — every opcode and field order it uses is transcribed into
`bot/BsgoBot/Protocol/`, `World/` and `Bot/`, with the source noted in the comments.

## Scope

This targets a server you run yourself. Don't point it at someone else's.
