# Command Categories - Test Coverage Analysis

## Total Commands: 99

### 🌐 Web Browsers & Apps (28 commands)
- hey paicom open the browser
- hey paicom open redit
- hey paicom open twitch
- hey paicom open twiter
- hey paicom open the viarchat website
- hey paicom open spotify
- hey paicom open youtube
- hey paicom open discord
- hey paicom open gmail
- hey paicom open roblox
- hey paicom open disney plus
- hey paicom open the trading app
- hey paicom open paypal
- hey paicom open hulu
- hey paicom open hbo
- hey paicom open facebook marketplace
- hey paicom open vinted
- hey paicom open ebay
- hey paicom open aliexpress
- hey paicom open facebook
- hey paicom open blue sky
- hey paicom open new grounds
- hey paicom open poki
- hey paicom open fur affinity
- hey paicom open pinterest
- hey paicom open pony town
- hey paicom open insta grem
- hey paicom open fiver

### 🎮 Steam Integration (7 commands)
- hey paicom hide my online status on steam
- hey paicom put my steam status online
- hey paicom open the steam chat
- hey paicom open my steam library
- hey paicom show my steam friends
- hey paicom start the steam vr mode
- hey paicom show my steam friends

### 🎵 Music Control (6 commands)
- hey paicom play some music
- hey paicom play relaxing music
- hey paicom pause the music
- hey paicom resume the music
- hey paicom play the next song
- hey paicom play the previous song
- hey paicom play the previous song on spotify

### 🔊 Volume Control (2 commands)
- hey paicom volume up
- hey paicom volume down

### 🔍 Location/Places Search (19 commands)
- hey paicom show restaurants near me
- hey paicom hows the weather
- hey paicom show cafes near me
- hey paicom show pizza places near me
- hey paicom show bars near me
- hey paicom show cool places near me
- hey paicom show museums near me
- hey paicom show parks near me
- hey paicom show cinemas near me
- hey paicom show malls near me
- hey paicom show bowling areas near me
- hey paicom show arcades near me
- hey paicom show tourist attractions near me
- hey paicom show hiking places near me
- hey paicom show beaches near me
- hey paicom show camping areas near me
- hey paicom show gas stations near me
- hey paicom show banks near me

### 💬 Conversation/Chat (18 commands)
- hey paicom I have no bitches
- hey paicom someone made a joke about me
- hey paicom why are you calling me a researcher
- hey paicom you suck
- hey paicom do you know my nickname
- hey paicom should I eat food
- hey paicom I'm bored
- hey paicom prezent yourself
- hey paicom are you straight
- hey paicom are you a boy kisser
- hey paicom show me a cool magic trick
- hey paicom can you hear me
- hey paicom recommend me a game
- hey paicom i dont have sus games
- hey paicom I am very horny
- hey paicom do you like chat gi bi ti
- hey paicom you are delusional
- hey paicom where is your charging port
- hey paicom is your charging port sensible
- hey paicom ai

### 🎮 Games/Activities (4 commands)
- hey paicom lets play top or bottom
- hey paicom lets play rock paper scissors
- hey paicom lets play tic tac toe
- hey paicom show me a meme

### 🛠️ System/Utility (6 commands)
- hey paicom open task manager
- hey paicom stop chrome
- hey paicom shut down
- hey paicom calibrate my trackers
- hey paicom switch to the vr headset microphone
- hey paicom become a background process
- hey paicom become a top process

### 🎭 Personality/Show (3 commands)
- hey paicom please hide
- hey paicom please show up
- hey paicom stop listening

### ⏰ Information (2 commands)
- hey paicom what time is it

---

## Test Execution Strategy

### Phase 1: Core Commands (High Priority)
Test the most commonly used commands first:
- Browser (1)
- Music control (6)
- Volume (2)
- Steam (7)
- Task manager (1)

**Total: 17 commands**

### Phase 2: Web Apps (Medium Priority)
Test all web application launches:
- All 28 browser/app commands

### Phase 3: Location Search (Medium Priority)
Test all "near me" location queries:
- All 19 location commands

### Phase 4: Chat/Conversation (Low Priority)
Test personality/chat responses:
- All 18 conversation commands

### Phase 5: Games & Activities (Low Priority)
Test interactive games:
- All 4 game commands

### Phase 6: System & Utility (Low Priority)
Test system utilities:
- All 6 system commands

---

## Running Specific Category Tests

### Test only web browsers:
```bash
./test-all-commands.sh --command \
  "hey paicom open the browser" \
  "hey paicom open redit" \
  "hey paicom open twitch" \
  --interval 2000
```

### Test only music commands:
```bash
./test-all-commands.sh --command \
  "hey paicom play some music" \
  "hey paicom pause the music" \
  "hey paicom resume the music" \
  "hey paicom play the next song" \
  "hey paicom play the previous song" \
  --interval 2000
```

### Test only steam commands:
```bash
./test-all-commands.sh --command \
  "hey paicom hide my online status on steam" \
  "hey paicom put my steam status online" \
  "hey paicom open the steam chat" \
  "hey paicom open my steam library" \
  "hey paicom show my steam friends" \
  "hey paicom start the steam vr mode" \
  --interval 2000
```

### Test only location commands:
```bash
./test-all-commands.sh --command \
  "hey paicom show restaurants near me" \
  "hey paicom show cafes near me" \
  "hey paicom show pizza places near me" \
  "hey paicom show bars near me" \
  --interval 2000
```

---

## Expected Results

For each command, you should see:
```
[TEST] (00:00.12) Injected #1: "hey paicom open the browser"
[TEST]   -> Dispatch: QUEUED, sent to game IPC queue
```

In the game, you should observe:
- ✅ Animation plays (character moves, emotes, etc.)
- ✅ Audio feedback (TTS response)
- ✅ Action executed (browser opens, music plays, etc.)

If a command fails, check:
1. Command text matches exactly (case-sensitive)
2. Token file exists in custom-commands folder
3. Handler method is found by reflection
4. Fallback script exists (if using process fallback)
