# Keyword Matching - Quick Demo

## Test These Commands

All of these will now correctly match their target command, even if Vosk misrecognizes them:

### Test 1: Just the keyword
```
Vosk hears: "roblox"
Match: "hey paicom open roblox" (95% - keyword match)
```

### Test 2: Keyword with extra words
```
Vosk hears: "hey paicom open roblox please"
Match: "hey paicom open roblox" (95% - keyword match)
```

### Test 3: Keyword buried in sentence
```
Vosk hears: "hey paicom i want to play roblox"
Match: "hey paicom open roblox" (95% - keyword match)
```

### Test 4: Misrecognized keyword (typo)
```
Vosk hears: "hey paicom spotfiy" (wrong spelling)
Match: "hey paicom open spotify" (90% - bigram similarity)
```

### Test 5: Multiple keywords
```
Vosk hears: "hey paicom open youtube and spotify"
Match: First unique keyword wins (youtube or spotify)
```

## Hard Words That Now Work Better

These are the words Vosk typically struggles with, now enhanced:

| Hard Word | Why It's Hard | Now Matches Via |
|-----------|---------------|-----------------|
| roblox | Unusual brand name | Keyword index |
| spotify | Brand name, often misrecognized | Keyword + bigram |
| aliexpress | Long compound word | Keyword index |
| vinted | Uncommon word | Keyword index |
| furaffinity | Compound word | Keyword index |
| newgrounds | Compound word | Keyword index |
| poki | Short brand name | Keyword index |
| hbo | Very short acronym | Keyword index |
| vinted | European app, unusual | Keyword index |
| fur affinity | Two words, often merged | Keyword index |
| insta grem | Creative spelling | Keyword index (instagram) |
| vrchat | Brand acronym | Keyword index |
| disney plus | Two-word brand | Keyword index (disney) |
| facebook marketplace | Long phrase | Keyword index (marketplace) |
| bluesky | Compound word | Keyword index |

## Run Demo

```bash
# Quick test with just the hard words:
dotnet run --project CrossPlatformPatcher.csproj -c Release -- \
  --test-commands PAIcom_Player_Folder/PAIcom_patched_test.exe \
  --interval 3000 \
  --command \
    "hey paicom roblox" \
    "hey paicom spotify" \
    "hey paicom aliexpress" \
    "hey paicom vinted" \
    "hey paicom open fur affinity" \
    "hey paicom new grounds" \
    "hey paicom poki" \
    "hey paicom hbo" \
    "hey paicom disney plus" \
    "hey paicom open the viarchat website"
```

Watch for:
- Game should respond to EACH command
- Even if you just say "roblox" without "open"
- Even with extra words like "please" or "now"
