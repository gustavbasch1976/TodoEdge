TodoEdge v0.1
=============

A to-do list that lives on the edge of your screen. Claude reads screenshots of emails,
WhatsApp or Teams messages and turns them into to-do items.


Build and run (one time, about 5 minutes)
-----------------------------------------

1. Install the .NET 8 SDK. Open Terminal and run:

       winget install Microsoft.DotNet.SDK.8

2. Unzip this folder somewhere permanent, for example C:\Apps\TodoEdge.
3. Double-click build.bat. It builds the app and starts it.
   The finished app is publish\TodoEdge.exe. Pin or copy that file wherever you like.
4. Get an API key at https://console.anthropic.com/settings/keys (needs a little prepaid credit).
5. Right-click the TodoEdge icon, choose Settings, paste the key, tick
   "Start TodoEdge when Windows starts", and Save.


How to use it
-------------

Edge icon
- A thin blue bar sits at the top of the screen. Hover over it and it becomes a round button.
- Click: open or close the list.
- Drag: move it. It snaps to the nearest edge of whichever monitor you drop it on.
- Right-click: menu (screenshot, clipboard, add item, settings, quit).
- Badge: number of open items. Red means something is overdue.

List
- Type at the bottom and press Enter, or click +.
- Tick a checkbox to complete an item. An Undo bar shows for 6 seconds.
- Double-click an item to edit it.
- Right-click an item: due date, tag, move to top, show source, delete.
- Right-click empty space: add item, add from screenshot, add from clipboard, clear completed.
- "Show completed" at the bottom lists finished items. Untick one to restore it.

AI capture (works from any app)
- Ctrl+Shift+T: the screen dims. Drag a box round the email or message. Click once to use the whole monitor.
- Ctrl+Shift+Y: reads whatever you copied (selected text, a copied image, or an image file).
- Drop an image file or selected text onto the edge icon.
- Ctrl+V while the list is open.
- Claude shows what it found. Untick, edit titles, dates and tags, then click "Add selected".
- Each item keeps its source. Right-click > Show source opens the original screenshot.

Tray icon
- Left-click opens the list. Right-click has the same options, plus "Move icon back to top of screen"
  and "Open data folder".


Where things are stored
-----------------------

%AppData%\TodoEdge
- tasks.json      your list
- settings.json   settings; the API key is encrypted with your Windows account (DPAPI)
- sources\        screenshots behind each item

To back up or move to another PC, copy tasks.json and the sources folder.
The API key will not decrypt on another PC, so re-enter it there.


Cost and privacy
----------------

- Default model is claude-haiku-5-5, the fastest and cheapest. Switch to claude-sonnet-5-5 in Settings
  if it misreads long or messy screenshots.
- A typical screenshot is around 1,000 to 2,000 input tokens, so roughly a penny or less per capture.
  Check current prices at https://www.anthropic.com/pricing.
- Screenshots and text go to Anthropic's API only when you trigger a capture. Nothing is sent otherwise.
  Check your employer's policy before capturing confidential work emails.


Known limits in v0.1
--------------------

- Windows does not allow one app to add items to other apps' right-click menus, so capture uses
  the shortcuts, the icon menu and drag-and-drop instead.
- Dragging an email straight out of Outlook onto the icon is not supported yet. Use Ctrl+Shift+T.
- If another app already uses Ctrl+Shift+T or Ctrl+Shift+Y, TodoEdge tells you at start-up.
  Use the icon menu instead, or ask for the shortcuts to be made configurable.
- Light theme only.


Project layout
--------------

App.xaml / App.xaml.cs        start-up, single instance
AppController.cs              connects everything, Claude round trip
Windows\EdgeWindow.cs         docked edge icon, hover, drag and snap
Windows\TodoPanel.xaml(.cs)   the list
Windows\CaptureOverlay.cs     drag-to-select screen capture
Windows\ReviewWindow.xaml     confirm what Claude found
Windows\SettingsWindow.xaml   API key, model, tags, start-up
Services\ClaudeService.cs     API call and the extraction prompt
Services\TaskStore.cs         items, save and load
