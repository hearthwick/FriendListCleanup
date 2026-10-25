# Friend List Cleanup

A Dalamud plugin for FINAL FANTASY XIV that flags friends who have probably stopped playing, so you can make room when you're stuck at the 200-friend cap.

- Reads your in-game friend list, including "(Unable to Retrieve)" entries for deleted characters
- When you press **Scan**, checks each friend's public Lodestone page: achievements, job levels, gear, collections and more
- Saves a fingerprint of each profile, so a later scan can tell whether anything changed, even for friends with hidden achievements
- Gives each friend a verdict with the reason: character likely deleted, likely inactive, possibly inactive, can't tell, new friend, active
- Remove a friend from the list in one click (or Ctrl+click), through the game's own menu and confirmation

Command: `/flc` (or `/flcleanup`)

Status: early testing (v0.1).

## Installing (testers)

1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this link into the empty box, click **+**, then **Save**:
   `https://raw.githubusercontent.com/hearthwick/FriendListCleanup/main/repo.json`
3. Type `/xlplugins`, search for **Friend List Cleanup** and install it.
4. Type `/flc`.

Updates arrive through Dalamud like any other plugin.

## Building

1. Open `FriendListCleanup.slnx` in Visual Studio.
2. Build → Build Solution (Ctrl+Shift+B).
3. In game: `/xlsettings` → Experimental → Dev Plugin Locations → add the full path to
   `FriendListCleanup\bin\x64\Debug\FriendListCleanup.dll`, save, then enable it in `/xlplugins` → Dev Tools.

## Releasing a new version

1. Raise `<Version>` in `FriendListCleanup/FriendListCleanup.csproj` (for example 0.1.0.0 → 0.1.1.0).
2. In Visual Studio: Git Changes → Commit All → Push.

GitHub then builds the plugin, publishes release `v0.1.1.0` with `latest.zip`, and updates `repo.json` so testers get the update
(see `.github/workflows/release.yml`; progress shows under the repo's **Actions** tab).
Because that last step is a commit made on GitHub, **Pull** in Visual Studio before your next change.
