# Hardware: what to put in front of the sound card

You do not need any of this to run the software. A bare wire into line-in is
enough for VLF. This document is about what to add when you want more, ordered
by how much it costs and how much it buys.

## Level 0 — a wire (free)

A few metres of any insulated wire into the **line input** (not mic — mic input
has a DC bias that can be noisy and a narrower filter on some cards).

Works for: DCF77 at 77.5 kHz, RSDN-20 at 12–15 kHz, sferics, whistlers, SAQ
when it transmits.

Tips:

- Longer is not always better; a wire that picks up your switching power supply
  is worse than a short one.
- Ground the shield, not the signal. If the card has a 3.5 mm jack, the sleeve
  is ground.
- Keep the wire away from USB cables and monitors. VLF is dominated by local
  noise, and almost all of it is yours.
- Use `-b24`. At VLF the signal is often 40 dB below the local noise; 16-bit
  capture puts the quantisation floor right where you are trying to listen.

## Level 1 — an antenna that is not a wire (~500 ₽)

VLF wants **E-field**, not magnetic loop, for a first attempt: a short vertical
with a high-impedance buffer.

```
        │  ~1 m rod or wire
        │
     ┌──┴──┐
     │ 1 M │        ┌──────────┐
     │     ├────────┤ 100 nF   ├──► line in
     └──┬──┘        └──────────┘
        │
       GND
```

A 1 MΩ resistor to ground and a DC-blocking capacitor. That is the whole
circuit. It converts the antenna's high impedance to something the card's input
can drive.

For DCF77 specifically, a **ferrite rod with a coil** works better than a wire,
because the signal is H-field at that frequency and the noise is mostly E-field:

- 10 cm ferrite rod (or a stack of two)
- 100 turns of 0.2 mm wire
- a parallel capacitor to resonate at 77.5 kHz: `C = 1 / (4π²f²L)`

Measure the coil inductance (or estimate it) and compute the capacitor. A
resonant loop gives 20–30 dB over a bare wire, which is the difference between
"maybe" and "every minute, reliably".

## Level 2 — a downconverter for HF (~1000–2000 ₽)

This is where you leave VLF and get 80m/40m/20m amateur bands, shortwave
broadcast, and time signals. A **direct-conversion** front end:

```
antenna ──► band-pass ──► mixer ──► low-pass ──► I ──► sound card L
                            ▲                    └─► Q ──► sound card R
                            │
                        local oscillator
```

The classic sound-card SDR design (SoftRock) uses:

- **Si5351** clock generator as the local oscillator (I²C, cheap, covers 8 kHz–160 MHz)
- **74AC74** as a divide-by-4 quadrature generator, giving exact 90° I/Q
- **Tayloe / QSD detector** — a switched-capacitor mixer that is the actual trick
  behind every sound-card SDR

With a 192 kHz card this gives ±96 kHz of visible bandwidth, which fits one
broadcast FM station completely. That is the setup where `radiocity web --iq`
finally makes sense.

**This is the path the software now supports.** `radiocity web --iq --rate 192000
-b24` opens the card as stereo and treats it as a complex digitiser. Two things
to expect from real hardware:

- **The two channels will not match.** Expect 1–5% gain difference and a couple of
  degrees of phase error, which puts an image 26–40 dB down. The receiver corrects
  this automatically (`IqImbalance`); `--no-imbalance` turns it off so you can
  hear what it is doing.
- **A 90° phase split from a flip-flop is exact**, but the audio path afterwards
  is not. Keep the two channels' cables the same length, and if the card has a
  per-channel gain control, leave both at the same setting.

Cost: the parts are cheap; the PCB and the soldering are the real expense.
Kits exist (SoftRock Ensemble, and various Chinese clones).

## Level 3 — an RTL-SDR dongle (~1500–3000 ₽)

If the goal is *listening* rather than *building*, buy one of these and skip
levels 1 and 2.

- RTL-SDR v3/v4: 24 MHz–1.7 GHz, 2.4 MHz of bandwidth, ~8 bit
- Airspy: better dynamic range, more expensive
- SDRplay: better still

**This software does not drive them** — they need libusb and their own drivers,
which is exactly what this project set out to avoid. But it is worth saying
plainly: if you want to hear 87.5 MHz this weekend, this is the honest answer.

The DSP in this repository is not wasted, though — the demodulator, AGC, SSB
phasing and DCF77 decoder are all hardware-agnostic. Replacing `WinMM.cs` with an
RTL-TCP client would reuse everything else.

## What to expect, by setup

| Setup | Bands | What you will hear |
|---|---|---|
| Bare wire, 192 kHz | 0–96 kHz | DCF77 (maybe), sferics, whistlers |
| Ferrite rod + cap, 192 kHz | 77.5 kHz | DCF77 reliably, every minute |
| Downconverter, 192 kHz, `--iq` | ±96 kHz complex around the LO | AM broadcast, SSB amateurs, CW, utilities, **broadcast FM — mono and stereo** |
| RTL-SDR | 24 MHz–1.7 GHz | everything, but with different software |

## A reality check on noise

At VLF the limiting factor is almost never the receiver. It is:

1. **Switching power supplies** — phone chargers radiate from 20 kHz up. Unplug
   them and watch the noise floor drop.
2. **LED and fluorescent lamps** — broadband hash.
3. **Your own PC** — the CPU, the display cable, the USB bus. A laptop on
   battery is dramatically quieter than a desktop on mains.
4. **Ground loops** — if the antenna ground and the PC ground differ, you get
   50/100 Hz hum and harmonics all the way up.

Run `radiocity scan 5 192000` with everything unplugged, then plug things back
one at a time. The waterfall tells you exactly which one it was.
