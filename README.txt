TailRemote

Control another Windows PC with your keyboard, and hear everything it plays. Built for NVDA. Works over Tailscale or straight over the internet; everything is encrypted either way.

Files
- bin\arm64\TailRemote.exe for ARM64 PCs, bin\x64\TailRemote.exe for x64 PCs. Each is one self-contained file; no .NET install needed.

On the PC you want to control (the host)
1. Either install Tailscale on both PCs and sign in to the same tailnet, or use the host's internet address.
2. Run TailRemote, choose Mode: Host, type a password, press Start hosting.
3. A cloud PC usually has no sound card. TailRemote notices and sets up an audio device called TailRemote: approve the administrator prompt, then press Install Driver in VB-Cable's window. A progress bar shows the rest. You can also press Set up audio device (Alt D) at any time, and Remove audio device (Alt V) takes it off again.
4. If Windows Firewall would keep other PCs out, TailRemote offers to open the port. Port editor (Alt E) lists every port TailRemote has used and opens or closes them.
5. Check Start hosting when Windows starts. It asks for administrator once, then starts hosting at every sign-in as administrator, so keys also reach administrator windows and the port is opened by itself.

On your PC
1. Run TailRemote, choose Mode: Control another PC.
2. Type the host's Tailscale name, 100 address or internet address, the same port and password, and press Connect.
3. In the TailRemote window, press Control Shift Enter to control the remote PC. Every key goes there: Windows, Alt Tab, your NVDA key, everything.
4. Press Control Shift Enter again to come back to this PC.

Settings in the main window
- Output device: where the remote PC's sound plays here.

How it keeps delay down
- Audio is raw 16-bit 44.1 kHz stereo PCM, about 1.4 megabits per second while sound plays, nothing when it is silent. No codec, so no encoding delay.
- Audio goes over UDP in 5.8 millisecond packets and is never waited for or resent.
- The buffer sets itself. TailRemote measures how late packets arrive and holds just enough audio to cover the worst of the last 15 seconds. When the network gets rough it grows at once; when it calms down it shrinks again.
- The buffer is steered by playing up to half a percent faster or slower, which you cannot hear, so the audio is never cut to catch up. Only a pile-up after a long network stall is cut.
- A lost packet fades out instead of clicking.
- Playback uses the smallest audio period the sound driver offers.
- Keys go over TCP with no batching, so each key is sent the moment it is pressed.
- The window title shows the ping and the total audio delay. Press NVDA T to hear it. Run tailscale ping with the host's name: if it says via DERP, the connection is relayed and slower; a direct connection is best.

Updates
- Check for updates (Alt U) gets the newest version from github.com/ce2004/tailremote, checks the download, swaps it in and restarts. A host restarts hosting; a connected PC reconnects.
- To update the cloud PC, control it, switch to its TailRemote window and press Check for updates there. Your side reconnects by itself when it comes back.
- If the connection drops for any reason, TailRemote keeps trying every 2 seconds until it is back or you press Stop reconnecting.

Security
- Both PCs prove they know the password before anything is sent, and wrong passwords are slowed down.
- After that, keys, audio and messages are encrypted with keys that are new for every connection, and anything changed on the way is thrown away.
- Use a long password if the port is open to the internet.

Limits
- Control Alt Delete and Windows L are kept by Windows and never reach the remote PC.
- UAC prompts on the remote PC appear on the secure desktop, which no remote app can type into unless the remote PC turns off the secure desktop for UAC.
- If the host signs out or locks, keys stop working until it is signed in again.

Releasing
- Add a section to CHANGES.txt: the version number on its own line, then one change per line.
- Push a tag such as v1.0.1. GitHub builds both versions and publishes the release.

About the audio device
- It is VB-Cable by VB-Audio, free donationware: www.vb-cable.com. Its licence does not allow other programs to install it silently, so you press its Install Driver button yourself. TailRemote downloads it from vb-audio.com, checks it is signed by VB-Audio, then names it TailRemote and makes it the default output.
- If Windows needs a restart after installing it, restart and start hosting: TailRemote finishes the setup by itself.

Building
- build.bat builds bin\arm64 and bin\x64. If TailRemote is running from there, the new build replaces it, closes the old copy and restarts, still hosting or connected.
- build.bat arm64 or build.bat x64 builds one.
- TailRemote.exe --selftest runs a host and client on this PC and writes the result to tailremote-selftest.txt in the temp folder.
- TailRemote.exe --licence writes the NVDA controller client licence beside the exe.
