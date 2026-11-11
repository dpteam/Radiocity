# Radio theory, as far as this project needs it

Short version: a sound card can be an SDR, but only for what fits in its
bandwidth. Here is what that means in numbers, so nobody has to take it on
faith.

## Why 87.5 MHz is impossible and 77.5 kHz is not

Nyquist says a sampled signal can represent frequencies up to half the sample
rate. That is the entire constraint, and no amount of clever software moves it.

| Card rate | Band the ADC can see | |
|---|---|---|
| 44.1 kHz | 0 – 22.05 kHz | voice band only |
| 48 kHz | 0 – 24 kHz | VLF, some LF |
| 96 kHz | 0 – 48 kHz | LF |
| 192 kHz | 0 – 96 kHz | LF, VLF, DCF77 |

Broadcast FM sits at 87.5–108 MHz. That is 900× above the top of a 192 kHz
card. There is no filter, no trick, no clever demodulator that recovers it,
because the samples simply do not contain that information.

What *is* required is a **frequency converter**: a mixer with a local oscillator
that subtracts the carrier frequency, moving 87.5 MHz down to somewhere the card
can hear. That is a handful of components, and once it exists, the DSP in this
repository handles the rest unchanged.

## What a bare wire actually receives

0–96 kHz is not empty. It is **VLF and LF**, and it is genuinely interesting:

| Frequency | Signal | Notes |
|---|---|---|
| 11.9–14.9 kHz | RSDN-20 / "Alpha" | Russian navigation, three phases |
| 17.2 kHz | SAQ Grimeton | 1924 alternator transmitter, fires a few times a year |
| 19.6, 20.3, 22.1, 24.0 kHz | naval LF | MSK, mostly encrypted |
| 40.0, 60.0, 77.5 kHz | time signals | JJY, WWVB, DCF77 |
| 77.5 kHz | **DCF77** | decodes to a real clock; see below |
| 0.5–10 kHz | sferics, whistlers | lightning and magnetosphere, always present |

DCF77 at 77.5 kHz is the best first target: it is only 1.5 kHz below the top of
a 192 kHz card's band, it is on the air continuously, and success is unambiguous
— either you get the date and time or you do not.

Caveat: many codecs apply an anti-alias filter that starts rolling off well
before Nyquist. Some cards are deaf above 70 kHz. If DCF77 does not appear, try
a different card or a different input (line-in often has a wider filter than
mic-in).

## The sound card is a mediocre radio

Honest limitations, in the order they will annoy you:

1. **Dynamic range.** A 24-bit codec gives ~110 dB. A strong local station
   raises the noise floor across the whole band, because the ADC has one
   converter for everything. A real SDR has the same problem; it is not unique
   to this approach.
2. **Clock accuracy.** "192000 Hz" is usually off by 20–50 ppm. Run
   `radiocity calib` against a known carrier and the waterfall stops drifting.
3. **No front-end filtering.** A wire picks up everything at once. Strong
   signals 50 dB above the one you want will still be there after the ADC.
4. **Latency.** MME is 20–60 ms. Fine for receive, bad for anything
   interactive.
5. **Two channels, one converter.** A stereo input gives you I and Q from a
   quadrature detector, which is how SoftRock-style SDRs work — but you need the
   hardware to produce them.

## How the DSP works, in one paragraph per mode

**AM.** The carrier is a constant tone; information rides on its amplitude. Mix
the carrier to 0 Hz and the complex baseband magnitude *is* the audio. One
`sqrt` per sample. The DC blocker afterwards removes the carrier term that the
envelope still contains.

**FM.** Information is in the instantaneous frequency, which is the derivative
of phase. Take the phase difference between consecutive samples —
`arg(z[n] · conj(z[n-1]))` — and you have it. Using `atan2` instead of the
common cross-product approximation keeps it amplitude-independent, so fading
does not turn into noise.

**SSB.** Only one sideband is transmitted and the carrier is suppressed, so an
envelope detector hears nothing useful. The phasing method rebuilds the missing
sideband: shift one component by 90° with a Hilbert transformer, then add or
subtract. Which one you add determines whether you hear USB or LSB, and the
rejection of the unwanted sideband is set entirely by the filter length.

**CW.** A Morse carrier is an unmodulated tone that switches on and off. It is
audible only if you beat it against another tone, so the receiver generates one:
a BFO shifts the carrier to 700 Hz and the keying becomes a tone you can hear.

**DCF77.** One bit per second, encoded as the *duration* of a carrier dip: 100 ms
means 0, 200 ms means 1, and second 59 has no dip at all — which is exactly the
frame marker. No demodulator is needed beyond an envelope detector and a
threshold.

## Where the numbers in the tests come from

| Number | Meaning |
|---|---|
| 109 dB | AM chain distortion, measured on a clean input. Below float32 resolution. |
| −107 dB | SSB sideband rejection with 127 Hilbert taps. |
| 62 dB | FM residual after the discriminator. |
| 1 ppm | clock calibration accuracy; 0.19 Hz at 192 kHz. |
| 30 ms | AGC detector window. Shorter modulates gain at 2× tone frequency. |
| 70 dB | FIR stopband, Kaiser β = 8.6. |

## Further reading

- Rick Lyons, *Understanding Digital Signal Processing* — the FIR/decimation
  chapters are the ones that matter here.
- The SoftRock and HPSDR projects — what a proper sound-card SDR front end
  looks like in hardware.
- `pi4dus`/`DCF77` decoder write-ups — the telegram layout, including the BCD
  gotcha.
- ITU-R TF.583 — the actual DCF77 specification.
