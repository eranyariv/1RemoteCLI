# Using it from your phone

Open **https://1remotecli-hub.azurewebsites.net** and sign in with the same Microsoft account you used for `1remote login`.

## Add it to your home screen

Do this. It is not decoration:

- **On iPhone and iPad it is the only way to get notifications.** Apple does not deliver web push to a Safari tab, at all, however the permission prompt turns out. Without this step the app can only tell you something needs you while you are already looking at it.
- It launches without Safari's chrome, so you get the whole screen for the terminal.
- The app shell is cached, so it opens instantly and on a bad connection.

**iPhone / iPad** — in Safari, tap **Share** → **Add to Home Screen** → **Add**, then open 1RemoteCLI from the home screen rather than the tab.

**Android** — in Chrome, open the menu and tap **Install app** (or **Add to Home screen**).

**Desktop** — use the install icon in the address bar.

The app detects where it is running and shows you these steps itself when they apply, so if the notifications card is telling you to install, believe it.

## Turn on notifications

Once you have opened it from the home screen, the card at the top says **"Get told when a session needs you"**. Tap it and allow notifications.

You have to tap. The browser will only accept a permission request that came from a real gesture, which is also why the app never asks on its own.

Now, when a session goes quiet at a prompt, your phone buzzes. Tap the notification and you land in that session, attached, already showing the question.

If the card says something else:

| Card | Meaning |
| --- | --- |
| **Notifications are on** | Done. Nothing to do. |
| **Add 1RemoteCLI to your Home Screen** | You are in a tab on iOS. Push cannot work here. Install it. |
| **Notifications are turned off** | You tapped *Don't Allow* at some point. The page can never ask again — only iOS Settings → Notifications, or the site settings in Chrome, can undo it. |
| **Notifications need iOS 16.4 or later** | Web push did not exist before then. Update iOS. |

## Driving a session

Tap a machine, then a session. The current screen appears immediately — the agent keeps a live model of the terminal and sends you a snapshot on attach, so you are not waiting for the program to print something before you can see where it is.

- **Type** — tap the screen; your keyboard comes up. Characters go straight to the program.
- **Special keys** — the bar above the keyboard has `Esc`, `Tab`, `⏎`, arrows, `^C`, `^D`, `^Z`. **More keys** opens the rest.
- **Modifiers** — tap `Ctrl` or `Alt` and it applies to the next key you press, the way a phone shift key works. No chording required.
- **Ctrl+C** is a first-class button because interrupting a runaway program from your phone is half the reason this exists.

Multiple phones can be attached to the same session at once, and so can you at your desk. Everyone sees the same screen and anyone can type. Nobody gets kicked off.

One thing to expect: attaching reshapes the terminal to your phone's size, so a program running in a wide window at your desk will visibly reflow. That is deliberate — it is the only way the program's own line wrapping fits your screen. When you detach, the previous shape is handed back.

## What the banners mean

| Banner | What happened | What to do |
| --- | --- | --- |
| **Reconnecting** | Connection dropped — a tunnel, a lift, switching from Wi-Fi to cellular. | Nothing. It backs off and retries, and re-attaches you where you were. |
| **Some output was missed** | The program produced more than the buffer holds while you were away. | Nothing is wrong; the screen you are looking at is current. Scrollback before that point is gone. |
| **This session has ended** | The program exited. | The exit code is shown. Close it. |

A coloured dot next to a session is its state: waiting for you, running, or ended.

## Battery, data and being realistic

Output is coalesced into about 30 frames a second, and only what changed is sent. A busy `npm install` costs a few kilobytes a second, not a megabyte. Idle sessions cost nothing at all beyond a WebSocket keepalive.

The phone will not hold the connection open forever in the background — that is the OS, not the app. This is exactly what notifications are for: you get told, you tap, it reconnects and re-attaches. Do not rely on leaving the app open in the background and expecting to be watching hours later.
