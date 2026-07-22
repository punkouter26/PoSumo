# PoSumo Fighter Roster (8)

Tournament roster (tournament mode itself not built yet — needs all 8 trained).
Code mirror: `Assets/Scripts/Systems/Systems_FighterRoster.cs`.

| # | Fighter | Behavior | Folder | Color | Status |
|---|---------|----------|--------|-------|--------|
| 1 | MATT | `Matt` | Matt_v01 | red | ✅ trained (`matt_sumo02`) |
| 2 | DAVE | `Dave` | Dave_v01 | green | ✅ trained (`dave_sumo01`) |
| 3 | NICK | `Nick` | Nick_v01 | blue | ⬜ placeholder |
| 4 | MAYA | `Maya` | Maya_v01 | purple | ⬜ placeholder |
| 5 | TONGTONG | `TongTong` | TongTong_v01 | gold | ⬜ placeholder |
| 6 | KIM | `Kim` | Kim_v01 | teal | ⬜ placeholder |
| 7 | TARO | `Taro` | Taro_v01 | indigo | ⬜ placeholder |
| 8 | HANA | `Hana` | Hana_v01 | pink | ⬜ placeholder |

TARO and HANA are stand-in names chosen to fill the 8-fighter bracket — rename
freely (folder + MANIFEST + roster entry) before training them.

Each placeholder's `MANIFEST.md` documents the exact steps to train and wire in
that fighter. The observation/action contract (41 obs / 13 actions) must stay
identical across all fighters so any pair can share an arena.
