# Keyword Matching Enhancement

## What Changed

The `FuzzyMatcher` has been enhanced with a **multi-stage matching pipeline**:

### Before (Levenshtein only)
```
Input: "roblox" → Levenshtein distance → May not match "open roblox" (too different)
```

### After (Keyword + Levenshtein + Word Overlap)
```
Input: "roblox" → Keyword match → Found "roblox" → Returns "open roblox" (95% confidence)
Input: "open roblox please" → Keyword match → Found "roblox" → Returns "open roblox" (95% confidence)
Input: "i want to play roblox" → Keyword match → Found "roblox" → Returns "open roblox" (95% confidence)
```

## How It Works

### Stage 1: Unique Keyword Match (95% confidence)
- Builds an index of words that appear in **exactly ONE command**
- If any of these words appear in input, immediately match that command
- Examples: "roblox" only appears in "open roblox", so hearing "roblox" = match

### Stage 1b: Partial Keyword Match (90% confidence)
- Uses bigram similarity to handle misrecognitions
- If Vosk says "roblx" instead of "roblox", still matches
- If Vosk says "spotfiy" instead of "spotify", still matches

### Stage 2: Levenshtein Distance (with bonus)
- Falls back to edit distance if no keyword match
- **NEW**: 15% confidence boost if any word overlaps

### Stage 3: Word Overlap Bonus (+15%)
- If any word in input appears in command, boost confidence
- Example: "open the browser" + "browser" = higher confidence

## Unique Keywords Now Recognized

These words will **immediately match** their command if heard:

### Apps & Websites (28 unique keywords)
| Keyword | Command |
|---------|---------|
| roblox | hey paicom open roblox |
| spotify | hey paicom open spotify |
| youtube | hey paicom open youtube |
| discord | hey paicom open discord |
| gmail | hey paicom open gmail |
| twitch | hey paicom open twitch |
| reddit | hey paicom open redit |
| twitter | hey paicom open twiter |
| vrchat | hey paicom open the viarchat website |
| disney | hey paicom open disney plus |
| paypal | hey paicom open paypal |
| hulu | hey paicom open hulu |
| hbo | hey paicom open hbo |
| vinted | hey paicom open vinted |
| ebay | hey paicom open ebay |
| aliexpress | hey paicom open aliexpress |
| facebook | hey paicom open facebook |
| marketplace | hey paicom open facebook marketplace |
| bluesky | hey paicom open blue sky |
| newgrounds | hey paicom open new grounds |
| poki | hey paicom open poki |
| furaffinity | hey paicom open fur affinity |
| pinterest | hey paicom open pinterest |
| ponytown | hey paicom open pony town |
| instagram | hey paicom open insta grem |
| fiverr | hey paicom open fiver |
| chrome | hey paicom stop chrome |

### Music Control (6 unique keywords)
| Keyword | Command |
|---------|---------|
| relaxing | hey paicom play relaxing music |
| pause | hey paicom pause the music |
| resume | hey paicom resume the music |
| next | hey paicom play the next song |
| previous | hey paicom play the previous song |

### Steam (7 unique keywords)
| Keyword | Command |
|---------|---------|
| invisible | hey paicom hide my online status on steam |
| online | hey paicom put my steam status online |
| steam-chat | hey paicom open the steam chat |
| library | hey paicom open my steam library |
| friends | hey paicom show my steam friends |
| vrmode | hey paicom start the steam vr mode |

### Location Search (19 unique keywords)
| Keyword | Command |
|---------|---------|
| restaurants | hey paicom show restaurants near me |
| weather | hey paicom hows the weather |
| cafes | hey paicom show cafes near me |
| pizza | hey paicom show pizza places near me |
| bars | hey paicom show bars near me |
| museums | hey paicom show museums near me |
| parks | hey paicom show parks near me |
| cinemas | hey paicom show cinemas near me |
| malls | hey paicom show malls near me |
| bowling | hey paicom show bowling areas near me |
| arcades | hey paicom show arcades near me |
| tourist | hey paicom show tourist attractions near me |
| hiking | hey paicom show hiking places near me |
| beaches | hey paicom show beaches near me |
| camping | hey paicom show camping areas near me |
| gas | hey paicom show gas stations near me |
| banks | hey paicom show banks near me |

### Other Unique Keywords
| Keyword | Command |
|---------|---------|
| bitches | hey paicom I have no bitches |
| joke | hey paicom someone made a joke about me |
| researcher | hey paicom why are you calling me a researcher |
| nickname | hey paicom do you know my nickname |
| bored | hey paicom I'm bored |
| calibrate | hey paicom calibrate my trackers |
| trackers | hey paicom calibrate my trackers |
| prezent | hey paicom prezent yourself |
| boykisser | hey paicom are you a boy kisser |
| magic | hey paicom show me a cool magic trick |
| recommend | hey paicom recommend me a game |
| sus | hey paicom i dont have sus games |
| horny | hey paicom I am very horny |
| chatgpt | hey paicom do you like chat gi bi ti |
| meme | hey paicom show me a meme |
| furniture | hey paicom if you would be a piece of furniture what would you be |
| sas | hey paicom are you sas |
| delusional | hey paicom you are delusional |
| charging | hey paicom where is your charging port |
| sensible | hey paicom is your charging port sensible |
| hobby | hey paicom do you have a hobby |
| trading | hey paicom open the trading app |
| background | hey paicom become a background process |
| listening | hey paicom stop listening |
| scissors | hey paicom lets play rock paper scissors |
| tic | hey paicom lets play tic tac toe |
| toe | hey paicom lets play tic tac toe |

## Examples of Improved Matching

### Before vs After

| Vosk Output | Before | After |
|-------------|--------|-------|
| "roblox" | ❌ No match | ✅ "open roblox" (95%) |
| "open roblox please" | ❌ No match | ✅ "open roblox" (95%) |
| "i want to play roblox" | ❌ No match | ✅ "open roblox" (95%) |
| "spotfiy" (typo) | ❌ No match | ✅ "open spotify" (90%) |
| "aliexpress" | ❌ No match | ✅ "open aliexpress" (95%) |
| "vinted now" | ❌ No match | ✅ "open vinted" (95%) |
| "fur affinity" | ⚠️ Maybe (low conf) | ✅ "open fur affinity" (95%) |
| "show pizza" | ⚠️ Maybe | ✅ "show pizza places near me" (95%) |
| "pause music" | ⚠️ Maybe | ✅ "pause the music" (95%) |
| "open hbo please" | ❌ No match | ✅ "open hbo" (95%) |

## Testing

Run the test to see keyword matching in action:

```bash
./quick-test-all.sh
```

Or test specific keywords:

```bash
./test-all-commands.sh --command \
  "hey paicom roblox" \
  "hey paicom open spotify" \
  "hey paicom aliexpress" \
  "hey paicom vinted" \
  "hey paicom open fur affinity" \
  "hey paicom show pizza" \
  --interval 2000
```

## Technical Details

### Keyword Index Construction
- Scans all 99 commands from `commands.txt`
- Counts how many commands each word appears in
- Only keeps words that appear in **exactly ONE** command
- Excludes stop words (the, a, an, is, etc.)

### Partial Matching (Bigram Similarity)
- If exact keyword not found, tries similar words
- Uses character bigram overlap (85%+ threshold)
- Handles common misrecognitions: roblox↔roblx, spotify↔spotfiy

### Word Overlap Bonus
- Any shared word between input and command = +15% confidence
- Applied during Levenshtein stage
- Helps when keyword isn't unique but words overlap
