# Generates radio/dispatch voice clips for Qualified Immunity using Windows' built-in
# TTS (System.Speech / SAPI). Each clip = [static squelch] + [spoken line] + [short squelch].
# Output WAVs go into the game's scripts\QI_Audio\ folder. Re-run after editing lines.
# The order here MUST match the Radio[] and Taunts[] arrays in the C# scripts.

Add-Type -AssemblyName System.Speech

$dir = "E:\SteamLibrary\steamapps\common\Grand Theft Auto V Enhanced\scripts\QI_Audio"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$rate = 22050
$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(`
    $rate, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$rnd = New-Object Random

function Get-WavData([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $i = 12
    while ($i -lt $bytes.Length - 8) {
        $id = [System.Text.Encoding]::ASCII.GetString($bytes, $i, 4)
        $sz = [BitConverter]::ToInt32($bytes, $i + 4)
        if ($id -eq 'data') {
            $out = New-Object byte[] $sz
            [Array]::Copy($bytes, $i + 8, $out, 0, $sz)
            return $out
        }
        $i += 8 + $sz + ($sz % 2)
    }
    return New-Object byte[] 0
}

function New-Noise([int]$ms, [int]$amp) {
    $n = [int]($rate * $ms / 1000)
    $b = New-Object byte[] ($n * 2)
    for ($k = 0; $k -lt $n; $k++) {
        $v = $rnd.Next(-$amp, $amp)
        $s = [BitConverter]::GetBytes([int16]$v)
        $b[$k * 2] = $s[0]; $b[$k * 2 + 1] = $s[1]
    }
    return $b
}

function Write-Wav([byte[]]$pcm, [string]$path) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes('RIFF'))
    $bw.Write([int](36 + $pcm.Length))
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes('WAVE'))
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes('fmt '))
    $bw.Write([int]16)
    $bw.Write([int16]1)            # PCM
    $bw.Write([int16]1)            # mono
    $bw.Write([int]$rate)
    $bw.Write([int]($rate * 2))    # byte rate
    $bw.Write([int16]2)            # block align
    $bw.Write([int16]16)           # bits
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes('data'))
    $bw.Write([int]$pcm.Length)
    $bw.Write($pcm)
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
}

function Make-Clip([string]$text, [string]$out) {
    $tmp = Join-Path $dir "_tmp.wav"
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $synth.Rate = 1
    $synth.Volume = 100
    $synth.SetOutputToWaveFile($tmp, $fmt)
    $synth.Speak($text)
    $synth.Dispose()
    $speech = Get-WavData $tmp
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    $lead = New-Noise 130 3500
    $tail = New-Noise 70 2500
    $all = New-Object byte[] ($lead.Length + $speech.Length + $tail.Length)
    [Array]::Copy($lead, 0, $all, 0, $lead.Length)
    [Array]::Copy($speech, 0, $all, $lead.Length, $speech.Length)
    [Array]::Copy($tail, 0, $all, $lead.Length + $speech.Length, $tail.Length)
    Write-Wav $all $out
}

$radio = @(
    "Dispatch, suspect failed to signal a turn. Requesting lethal force.",
    "I have not read him his rights, and I do not intend to!",
    "Be advised, I'm two coffees deep and I can see through time.",
    "Requesting backup, air support, and someone to hold my badge.",
    "Define excessive. Asking for a friend. The friend is me.",
    "Best shift ever! This is why I skipped the ethics module!",
    "Dispatch, vibes immaculate, suspect toast, paperwork pending.",
    "He looked at me funny back in twenty nineteen. It's personal now.",
    "Pursuit ongoing. I've decided the speed limit is a personal attack.",
    "Dispatch, can someone google if this is legal? Asking mid chase.",
    "Suspect signaled politely. Suspiciously polite. Floor it.",
    "I'm not angry, I'm just constitutionally unaccountable.",
    "Requesting a chopper, a tank, and a hug. In that order.",
    "He's doing the speed limit now. Cowardice. Open fire.",
    "Be advised, my body cam fell into my coffee. Again. Tragic.",
    "Reading him his rights would imply I can read. Negative.",
    "Suspect's crime? Vibes. Bad ones. Trust me, I'm trained.",
    "Backup, bring snacks. The only hostage here is my lunch.",
    "I haven't blinked since the academy and I'm not starting now.",
    "Dispatch, define unarmed. He had fists. Two of them!"
)
$taunt = @(
    "Resisting arrest, your honor.",
    "Qualified immunity, baby!",
    "He was reaching for something. Probably.",
    "Paperwork is gonna love this one.",
    "Shouldn't have flipped me off.",
    "Brotherhood protects its own.",
    "I felt threatened. From over there.",
    "Administrative leave, here I come!"
)
$welcome = @(
    "Buckle up. Statistically, someone's getting tased today. Might be you.",
    "Welcome aboard! House rules: no seatbelt jokes, the airbags are decorative.",
    "Glad you're here. If I yell GUN, it's probably my coffee. Probably.",
    "Ride-along waiver? Signed it for you. In crayon. You're covered. Maybe.",
    "Sit tight. Today's itinerary: a chase, a misunderstanding, and great snacks.",
    "Touch the radio and you walk home. Touch the siren and you're hired.",
    "Strap in. We do everything by the book - we just haven't read the book.",
    "Good to have you! If things get loud, that's just community outreach.",
    "Climb in. I haven't lost a ride-along yet. The paperwork would be brutal.",
    "Welcome aboard. Remember: everything I do today is, technically, legal-ish."
)
$pit = @(
    "Requesting permission to PIT, ah, who am I kidding.",
    "Dispatch, green light on the PIT? Too late! HAHA!",
    "Permission to PIT requested, denied, and ignored. PIT IT!",
    "I'll ask forgiveness, not permission, SENDING IT!",
    "Supervisor says negative on the PIT. Supervisor isn't HERE.",
    "Is a PIT authorized? Didn't ask, don't care, already did it!",
    "Permission to gently nudge him? Gentle enough!",
    "Filing the PIT request, and by filing I mean DOING IT! Whoops!"
)
$collateral_q = @(
    "Uh, dispatch, were there civilians in that crosswalk?",
    "Hey, were those bystanders we just went through?",
    "Should we be worried about the body count back there?",
    "That was a LOT of pedestrians. We good on that?"
)
$collateral_a = @(
    "Collateral damage is the price of excellence. WORTH IT.",
    "Eggs, omelettes, you know the saying. Totally worth it!",
    "They go in the report as scenery. Worth every penny.",
    "Acceptable losses! The suspect matters WAY more. Worth it!",
    "Suspect deliberately pushed a civilian into our path! Not our fault!",
    "Be advised, suspect just used a civilian as a human shield!",
    "Civilian was interfering with an active pursuit! Pushing through!"
)
# Keep in sync with SettlementQuips[] in QualifiedImmunity.cs.
$settlement = @(
    "Your taxes at work!",
    "Still cheaper than retraining!",
    "The officer has been placed on PAID leave.",
    "No wrongdoing was found. It never is.",
    "The city disputes the family's account.",
    "Budget line item: oopsies.",
    "Thoughts, prayers, and a non-disclosure agreement.",
    "Don't worry, the pension is safe."
)
# Keep in sync with IaVerdicts[] in QualifiedImmunity.cs.
$ia = @(
    "Investigation complete. The officer acted within policy. Elapsed time: six seconds.",
    "We have reviewed the footage. There is no footage.",
    "After carefully reading the officer's own statement, the officer is cleared.",
    "The deceased had a record: jaywalking, twenty fourteen. Use of force justified.",
    "Finding: the bullets acted independently of the officer.",
    "Case closed. The officer has been nominated for Employee of the Month."
)

for ($i = 0; $i -lt $radio.Length; $i++) { Make-Clip $radio[$i] (Join-Path $dir "radio_$i.wav") }
for ($i = 0; $i -lt $taunt.Length; $i++) { Make-Clip $taunt[$i] (Join-Path $dir "taunt_$i.wav") }
for ($i = 0; $i -lt $welcome.Length; $i++) { Make-Clip $welcome[$i] (Join-Path $dir "welcome_$i.wav") }
for ($i = 0; $i -lt $pit.Length; $i++) { Make-Clip $pit[$i] (Join-Path $dir "pit_$i.wav") }
for ($i = 0; $i -lt $collateral_q.Length; $i++) { Make-Clip $collateral_q[$i] (Join-Path $dir "collateral_q_$i.wav") }
for ($i = 0; $i -lt $collateral_a.Length; $i++) { Make-Clip $collateral_a[$i] (Join-Path $dir "collateral_a_$i.wav") }
for ($i = 0; $i -lt $settlement.Length; $i++) { Make-Clip $settlement[$i] (Join-Path $dir "settlement_$i.wav") }
for ($i = 0; $i -lt $ia.Length; $i++) { Make-Clip $ia[$i] (Join-Path $dir "ia_$i.wav") }

Write-Output ("Generated {0} clips into {1}" -f ($radio.Length + $taunt.Length + $welcome.Length + $pit.Length + $collateral_q.Length + $collateral_a.Length + $settlement.Length + $ia.Length), $dir)

