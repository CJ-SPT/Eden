EDEN CLIENT DOWNPATCHER
Windows x64, EVE Online build @BUILD@

1. Extract the ZIP. This tool includes its own Python runtime and downloads
   game files from CCP as needed. An existing EVE installation, internet
   access, and about 46 GiB of free space are required.
2. In the official EVE Launcher, open Settings > EVE Online > Browse Game
   Files. Locate the SharedCache folder containing both tq and ResFiles.
3. Run Start-Patcher.cmd. Enter the SharedCache path and then a separate,
   empty destination such as D:\EdenClient\@BUILD@. The live cache is read-only.
4. Wait for "Eden client build @BUILD@ is ready". After an interruption,
   run Start-Patcher.cmd with the same paths to resume.
5. In Eden Launcher, open Client settings, select the completed destination,
   and choose Use this client.

CCP SharedCache guide
https://support.eveonline.com/hc/en-us/articles/5885307869468-Shared-Cache
