# Tushonkapedia

A wiki for your own SPT server. Quests, items, traders, maps, bots, the hideout and crafts, all read from your server as it runs, so your mods are in it too. Pick your profile and it shows your progress.

For SPT 4.1.

## Install

1. Close the game and the server.
2. Extract the zip into your SPT folder.
3. Start the server.

## Open it

Click **Tushonkapedia** under **Mod Pages** in the SPT launcher, or go to **https://127.0.0.1:6969/wiki/** on the server PC.

The first time, your browser warns you about the page's certificate. SPT makes its own. Click **Advanced**, then **Continue**.

From another PC or a phone on your network, use **https://\<server IP\>:6969/wiki/**. For that, `"ip"` in `SPT_Runtime\SPT_Data\configs\http.json` has to be `0.0.0.0`.

## The game plugin

The plugin in `BepInEx\plugins\Tushonkapedia` is optional. It:

- draws pictures for items that have none (mostly mod items)
- draws your character for the **My character** page
- notes where containers, locked doors and quest places are on each map
- lets the wiki sell, repair, craft and upgrade through the game while the game is open

Each of these happens once, and again only when something changes. You can turn each one off in the F12 menu, under **Fums-TushonkaClient**.

## Your character

The wiki only changes your character when you ask it to (sell, repair, craft, upgrade, wishlist), and it asks you first.

## Settings

`config.json` in `SPT_Runtime\user\mods\Tushonkapedia`. Each setting is explained in the file.

## Uninstall

Delete these 2 folders:

- `SPT_Runtime\user\mods\Tushonkapedia`
- `BepInEx\plugins\Tushonkapedia`

## Build it yourself

```
.\build.ps1 -SPTPath D:\SPT
```

## License

[CC BY-NC-ND 4.0](LICENSE): share it unchanged, with credit, for no money. No changed versions without asking. The map drawings and the game's own pictures keep their own terms, listed in the license.

## Credits

Some item pictures, the map drawings and the quest keys come from [tarkov.dev](https://tarkov.dev). The map drawings are CC BY-NC-SA 4.0.

## Found a problem?

Report it on [GitHub issues](https://github.com/MrFums/Tushonkapedia/issues).
