# Anamnesis Data Path Trace

Source checked out locally at `external/Anamnesis`.

## Trace Summary

The useful Anamnesis route is not an `ActionTimelineId` parameter and not Brio's `ActionTimelineCapability`. The Data Path UI writes model-object fields before an animation/emote is played, so the game can resolve the same timeline id through a different character data path.

## Evidence

| Source | Finding | Risk |
| --- | --- | --- |
| `external/Anamnesis/Anamnesis/Actor/Pages/ActionPage.xaml` | The Data Path selector binds `DataHead` to `Actor.Unsafe.ModelObject.DataHead` and `DataPath` to `Actor.Unsafe.ModelObject.DataPath`. | This is not a PlayTimeline argument. It is actor model-object state. |
| `external/Anamnesis/Anamnesis/Memory/ActorModelMemory.cs` | `ActorModelMemory.Skeleton` is bound at `+0x0A0`, `DataPath` at `+0xAA0`, and `DataHead` at `+0xAA4`. | `DataPath`/`DataHead` are plausible animation resolver candidates, but writes must be experimental only. |
| `external/Anamnesis/Anamnesis/Actor/Views/DataPathSelector.xaml.cs` | Selecting a Data Path sets both `DataPath` and `DataHead`. `DataHead` is derived from the selected path and current tribe. | A valid experiment likely needs both fields, not only `DataPath`. |
| `external/Anamnesis/Anamnesis/GameData/DataPathResolver.cs` | `DataPaths` maps race/gender/NPC path ids such as `101`, `201`, `701`, `1101`, etc. `ToDataPath(tribe, gender, isNpc)` maps customize traits to these path ids. | This gives preset-to-path mapping, but writing customize Race/Gender/Tribe is still disallowed. |
| `external/Anamnesis/Anamnesis/Actor/Utilities/ExpressionPoseLibraryGenerator.cs` | The expression library enumerates `DataPaths` to generate race/gender specific expression pose data. | Useful for validating path ids; not a runtime rig override path. |

## Current Conclusion

`ActionTimelineId` replay proves only that animation playback happened. It does not prove that a selected rig participated in resource resolution. The next viable candidate is the actor model object's Data Path pair:

- `ActorModelMemory.DataPath` at `DrawObject + 0xAA0`
- `ActorModelMemory.DataHead` at `DrawObject + 0xAA4`

These fields are not Actor customize fields in Anamnesis, but they are still native model-object state. The plugin must not write them by default.

## Plugin Mapping

`AnimationDataPathCandidateScanner` now reports these candidates read-only:

- Anamnesis mapped candidates from the current actor `DrawObject`.
- Managed reflection candidates exposed by the Brio/actor wrapper, including `DataPath`, `DataHead`, `ModelObject`, `Skeleton`, `Timeline`, `Customize`, `Race`, `Gender`, and `Tribe`.
- Safety classification:
  - `CandidateAnimationOnly`: `DataPath` / `DataHead`.
  - `UnsafeAppearance`: customize/race/gender/tribe fields.
  - `ReadOnly`: skeleton/timeline/animation pointers.
  - `Unknown`: unmapped fields.

## Safe Next Step

Keep default mode read-only. A future experimental mode may test `DataPath`/`DataHead` only when manually enabled, and only with guards:

1. Resolve a Ready plugin Actor.
2. Snapshot appearance hash and world transform hash.
3. Snapshot candidate values.
4. Write candidate values.
5. Replay current timeline.
6. Verify animation binding/resource changed.
7. Verify appearance and transform did not change.
8. Restore immediately on `NoEffect` or any appearance/transform change.

No `Race/Gender/Customize` writes, no Penumbra redraw, no Actor recreate, and no skeleton pointer writes are permitted for this path.
