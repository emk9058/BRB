# BRB

BRB is a phone page for the League of Legends client on your PC. It stays on your home Wi-Fi. The phone talks to BRB, and BRB talks to League.

Use it from the party through champ select: change the mode, invite friends, queue, accept the match, hover, ban, and lock a champion. Spells, rune pages, and Swiftplay slots are on the phone too.

BRB is not made by Riot. It uses the League client already running on this computer.

## Install

Download `BRB-Setup.exe` and run it. Open **BRB** from the Start menu. A window shows a QR code and an address like `http://192.168.1.28:5105`. Scan it with a phone on the same Wi-Fi.

If Windows asks for network access, allow BRB on private networks. The first time you open an unsigned installer, Windows may say it protected your PC. Choose **More info**, then **Run anyway**.

The phone and the PC have to be on the same Wi-Fi. League can be opened before or after BRB.

## Make the download

From this folder:

```powershell
.\publish.ps1
```

That builds the phone page, packs it into the app, and writes `site\BRB-Setup.exe`. Put the `site` folder on the web host. The page and the installer need to stay next to each other.

You need the .NET 10 SDK, Node.js, and [Inno Setup 6](https://jrsoftware.org/isdl.php) to build the installer. People who download BRB do not.

## Run it from the project

Build the phone page once:

```powershell
cd web
npm install
npm run build
```

Start BRB:

```powershell
cd ..\host
dotnet run --launch-profile http
```

A window named BRB opens with a QR code and an address like `http://192.168.1.28:5105`. Scan the code with a phone on the same Wi-Fi, or type the address into the phone’s browser. Click the address in the window to copy it.

Leave BRB running while the phone is in use. Closing the window keeps it running. Right-click the BRB icon in the tray and choose **Quit** to stop it.

League can be opened before or after BRB. The window shows **Closed** until the client is running, then it shows the current screen, such as **Party** or **Champ select**.

## On the phone

The page follows the client.

- **Home.** Pick a game mode and start a party.
- **Party.** Invite friends, set roles, edit Swiftplay, and start the queue. League’s own messages show when the party cannot queue.
- **Queue.** Cancel the search.
- **Match found.** Accept or decline. The line shows the time left.
- **Champ select.** Hover a champion when it says to pick one. Ban and lock in on your turn. Teammate names show when League shows them. Hidden names stay the camp names, such as Gromp or Krug.
- **Spells and runes.** Change them from the loadout while you are in a party or champ select.

Refreshing the phone loads the latest page. If the PC address changes, scan the code again.

## While you are changing it

The phone page is a build, not the live Vue files. After a change in `web`, run `npm run build` again, then refresh the phone.

After a change in `host`, quit BRB and run `dotnet run --launch-profile http` again. If the port is already taken, the previous BRB is still running.

## Your network

BRB only listens on your LAN, on port **5105**. It is not on the internet. Anyone who can join that Wi-Fi can open the page while BRB is running, so use it on your own network. The phone never receives the League lockfile password.
