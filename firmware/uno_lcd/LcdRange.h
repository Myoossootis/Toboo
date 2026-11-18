// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#include <stdint.h>

inline uint16_t lcdDistanceMm(uint32_t echoUs) {
  if (echoUs > 25000UL) return 0;
  const uint32_t mm = echoUs * 343UL / 2000UL;
  return mm >= 20 && mm <= 4000 ? (uint16_t)mm : 0;
}
