TailRemote

Control another Windows PC over Tailscale with your keyboard, and hear everything it plays. Built for NVDA.

Files
- bin\arm64\TailRemote.exe for ARM64 PCs, bin\x64\TailRemote.exe for x64 PCs. Each is one self-contained file; no .NET install needed.

On the PC you want to control (the host)
1. Install Tailscale and sign in to the same tailnet.
2. It needs a sound output. A cloud PC usually has none: install a virtual audio device such as VB-Cable and make it the default output.
3. Run TailRemote, choose Mode: Host, type a password, press Start hosting.
4. Check Start hosting when Windows starts. It asks for administrator once, then starts hosting at every sign-in as administrator, so keys also reach administrator windows. It also adds the firewall rule.

On your PC
1. Run TailRemote, choose Mode: Control another PC.
2. Type the host's Tailscale name or 100 address, the same port and password, and press Connect.
3. In the TailRemote window, press Control Shift Enter to control the remote PC. Every key goes there: Windows, Alt Tab, your NVDA key, everything.
4. Press Control Shift Enter again to come back to this PC.

Settings in the main window
- Audio buffer in milliseconds: how much audio is held to smooth out the network. Lower means less delay; raise it if the sound crackles. 30 is the default.
- Output device: where the remote PC's sound plays here.
- Only accept Tailscale connections: refuse anything not from a Tailscale address.

How it keeps delay down
- Audio is raw 16-bit 44.1 kHz stereo PCM, about 1.4 megabits per second while sound plays, nothing when it is silent. No codec, so no encoding delay.
- Audio goes over UDP in 5.8 millisecond packets and is never waited for or resent. A late packet is dropped; if the buffer grows past its setting it is trimmed back.
- Playback uses the smallest audio period the sound driver offers.
- Keys go over TCP with no batching, so each key is sent the moment it is pressed.
- The window title shows the round trip time to the host. Run tailscale ping with the host's name: if it says via DERP, the connection is relayed and slower; a direct connection is best.

Limits
- Control Alt Delete and Windows L are kept by Windows and never reach the remote PC.
- UAC prompts on the remote PC appear on the secure desktop, which no remote app can type into unless the remote PC turns off the secure desktop for UAC.
- If the host signs out or locks, keys stop working until it is signed in again.

Building
- dotnet publish -c Release -r win-arm64 -o bin\arm64 -p:BaseOutputPath=obj\pubout\
- dotnet publish -c Release -r win-x64 -o bin\x64 -p:BaseOutputPath=obj\pubout\
- TailRemote.exe --selftest runs a host and client on this PC and writes the result to tailremote-selftest.txt in the temp folder.
- TailRemote.exe --licence writes the NVDA controller client licence beside the exe.
