// SPDX-License-Identifier: GPL-3.0-or-later
#ifndef TOBOO_LCD_FACE_H
#define TOBOO_LCD_FACE_H
#include "LcdProtocol.h"
#ifdef __AVR__
#include <avr/pgmspace.h>
#define LCD_FLASH PROGMEM
#else
#define LCD_FLASH
#endif

namespace LcdFace {
constexpr uint8_t glyphCount = 6;
using Frame = uint8_t[glyphCount][8];

struct Pose { int8_t x, y, eye, mouth; };
struct Keyframe { uint16_t time; Pose pose; };

#define CLIP(name) static const Keyframe name[] LCD_FLASH = {
#define POSE(time, x, y, eye, mouth) {time, {x, y, eye, mouth}},
#define END_CLIP() };
#include "LcdMotion.h"
#undef CLIP
#undef POSE
#undef END_CLIP
#undef LCD_FLASH

inline Keyframe read(const Keyframe& keyframe) {
#ifdef __AVR__
  Keyframe value;
  memcpy_P(&value, &keyframe, sizeof(value));
  return value;
#else
  return keyframe;
#endif
}

inline int8_t blend(int8_t from, int8_t to, uint16_t step, uint16_t duration) {
  const int32_t delta = (int32_t)(to - from) * step;
  return from + (delta + (delta < 0 ? -(int32_t)duration / 2 : duration / 2)) / duration;
}

template <unsigned N>
Pose sample(const Keyframe (&frames)[N], uint32_t elapsed) {
  const uint16_t time = elapsed % read(frames[N - 1]).time;
  Keyframe from = read(frames[0]);
  for (unsigned i = 1; i < N; ++i) {
    const Keyframe to = read(frames[i]);
    if (time <= to.time) {
      const uint16_t step = time - from.time, duration = to.time - from.time;
      return {blend(from.pose.x, to.pose.x, step, duration), blend(from.pose.y, to.pose.y, step, duration),
              blend(from.pose.eye, to.pose.eye, step, duration), blend(from.pose.mouth, to.pose.mouth, step, duration)};
    }
    from = to;
  }
  return from.pose;
}

inline Pose poseAt(LcdRole role, uint32_t elapsed) {
  switch (role) {
    case LcdRole::Attentive: return sample(Attentive, elapsed);
    case LcdRole::Thinking: return sample(Thinking, elapsed);
    case LcdRole::Responding: return sample(Responding, elapsed);
    case LcdRole::Happy: return sample(Happy, elapsed);
    default: return sample(Idle, elapsed);
  }
}

// The two eyes share four CGRAM slots. The mouth uses two more.
inline void frameAt(LcdRole role, uint32_t elapsed, Frame& glyphs) {
  const Pose pose = poseAt(role, elapsed);
  const int8_t top = 8 + pose.y - pose.eye / 2;
  const uint16_t curvedEye[] = {0x078, 0x0FC, 0x186, 0x303, 0x201};
  for (int8_t y = 0; y < 16; ++y) {
    uint16_t pixels = 0;
    const int8_t row = y - top;
    if (role == LcdRole::Happy) {
      const int8_t curve = y - 6 - pose.y;
      if (curve >= 0 && curve < 5) pixels = curvedEye[curve];
    } else if (row >= 0 && row < pose.eye) {
      pixels = pose.eye <= 2 || (row != 0 && row != pose.eye - 1) ? 0x0FC : 0x078;
      if (pose.x < 0) pixels <<= -pose.x;
      else pixels >>= pose.x;
    }
    const uint8_t slot = (y / 8) * 2;
    glyphs[slot][y % 8] = pixels >> 5;
    glyphs[slot + 1][y % 8] = pixels & 0x1F;
  }
  const uint16_t smiling[] = {0, 0x102, 0x102, 0x084, 0x078, 0, 0, 0};
  for (int8_t y = 0; y < 8; ++y) {
    uint16_t pixels = 0;
    if (role == LcdRole::Happy) {
      const int8_t row = y - pose.y;
      if (row >= 0 && row < 8) pixels = smiling[row];
    } else {
      const int8_t row = y - 4;
      if (row >= -pose.mouth && row <= pose.mouth)
        pixels = row == -pose.mouth || row == pose.mouth ? 0x078 : 0x084;
    }
    glyphs[4][y] = pixels >> 5;
    glyphs[5][y] = pixels & 0x1F;
  }
}
}
#endif
