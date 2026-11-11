# Drone Coverage

Adds a **Drone Coverage** line to the upgrade page of the tech tree. Each of its ten stages widens the area the construction and repair drones cover:

- Stages 1-9: +25% of the original range each (the mecha's 80 m becomes 100 m, 120 m ... 260 m).
- Stage 10: the whole planet.

It applies to the mecha and to battlefield analysis bases. The mecha's range is also its build range, so the build tools reach as far as the drones.

The line opens with the first construction drone upgrade. Costs grow like the game's own upgrades: blue cubes first, one more cube kind every two stages, all six on stage 10.

## Reset

Set `ResetResearch` (section `Reset` in `bug.dsp.dronecoverage.cfg`, editable in the mod manager) to `true`. Within a second the running game, or the next one loaded, goes back to stage 0: the upgrades can be researched again and the ranges return to the original. Spent cubes are not refunded. The setting turns itself back off.

## Compatibility

- Works on top of whatever range the game already has: if another mod (for example Multifunction's build area setting) changes the range, that value becomes the base the upgrades scale.
- Saves always store the original range, so removing the mod leaves the game's own ranges.
- No dependencies. Uses tech ids 9351-9356; if another mod already uses them, the upgrades are not added and the log says so.
- Placed in its own row under the lowest upgrade row, so it sits below DeliveryItemChain's rows when both are installed.

## Notes

- Planet-wide searches walk every object on the planet. They only run when the drones look for new work, but on very large factories the first stage-10 search can take a few milliseconds.

## Building from source

You need a current .NET SDK and the .NET Framework 4.7.2 targeting pack. The game, Unity and BepInEx assemblies are referenced from your own installation and are not part of this repository.

```text
dotnet build -c Release "-p:DSPGamePath=D:\Games\Dyson Sphere Program" "-p:BepInExCorePath=C:\path\to\BepInEx\core"
```

The output is `bin/Release/net472/BUG_DroneCoverage.dll`. Copy it to `BepInEx/plugins/BUG_DroneCoverage/`.

## License

MIT, see [LICENSE](LICENSE).
