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

Features
- Control Shift Enter in the TailRemote window switches between the remote PC and this one.
- Control Alt End sends Control Alt Delete to the remote PC, when it runs TailRemote as a service.
- Send files (Alt I) sends files either way. They arrive in Downloads, TailRemote, on the other PC.
- Copied text is shared between the PCs while Share clipboard text (Alt B) is checked.
- Restart remote PC and reconnect (Alt N) restarts the remote PC and reconnects when it is back.
- Saved PCs (Alt S): Save this PC (Alt H) remembers the address, port and password; Forget saved PC (Alt F) removes one.
- Listen-only password, in Host mode: anyone who connects with it hears the PC but cannot control it. Up to 100 at once. Each listener uses about 0.7 megabits per second of the host's upload while sound plays.

Running the host as a Windows service
- In Host mode, check Run as a Windows service (Alt C). It asks for administrator permission once.
- The service starts with Windows, before anyone signs in, and has full system access. You can use the lock screen, sign in, answer administrator prompts and send Control Alt Delete with Control Alt End.
- Anyone who knows the TailRemote password gets that access too, so use a long password.
- While the service runs, the hosting button becomes Apply settings to the service. Press it after changing the port or passwords.
- The service runs its own copy from Program Files. It never updates by itself: when you update TailRemote on that PC with Check for updates, the service follows straight away to the same version, without asking.
- Uncheck Run as a Windows service to remove it completely.

Settings in the main window
- Capture sound from (Host mode, Alt A): which output's sound the other PCs hear. Windows default follows whatever the default output is.
- Output device: where the remote PC's sound plays here.

How it keeps delay down
- Audio is 16-bit 44.1 kHz stereo, packed losslessly like FLAC: well under half of the 1.4 megabits per second raw sound would take, and on a struggling connection it steps the sample rate down, as far as 8 kHz, instead of lagging, nothing when it is silent, and well under a millisecond of delay.
- Audio goes over UDP in 5.8 millisecond packets and is never waited for or resent.
- The buffer is fixed at one packet plus 2 milliseconds and never grows. Audio that arrives late is skipped, so the delay always stays the same.
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
- Control Alt Delete and Windows L on your keyboard are kept by your own PC. Use Control Alt End for the remote PC.
- Without the service, the remote PC's lock screen and administrator prompts cannot be used from TailRemote.

Releasing
- Add a section to CHANGES.txt: the version number on its own line, then one change per line.
- Push a tag such as v1.0.1. GitHub builds both versions and publishes the release.

About the audio device
- It is VB-Cable by VB-Audio, free donationware: www.vb-cable.com. Its licence does not allow other programs to install it silently, so you press its Install Driver button yourself. TailRemote downloads it from vb-audio.com, checks it is signed by VB-Audio, then names it TailRemote and makes it the default output.
- If Windows needs a restart after installing it, restart and start hosting: TailRemote finishes the setup by itself.

Building
- build.bat builds bin\arm64 quickly, in seconds, without the release optimisations. build.bat x64 does the same for x64. build.bat full builds both exactly as a release is built.
- If TailRemote is running from bin, the new build replaces it, closes the old copy and restarts it, still hosting or connected.
- TailRemote.exe --selftest runs a host and client on this PC and writes the result to tailremote-selftest.txt in the temp folder.
- TailRemote.exe --licence writes the NVDA controller client licence beside the exe.
