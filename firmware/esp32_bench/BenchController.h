#pragma once

#include <stdint.h>
#include <stdio.h>
#include <string.h>

// Pure command-state simulation. This class has NO actuator transport.
namespace bench {
enum class State { Disarmed, Armed, TimedOut };

class Controller {
 public:
  State state = State::Disarmed;
  int pan = 900, tilt = 900, targetPan = 900, targetTilt = 900;
  uint32_t sequence = 0;
  bool hasSequence = false;

  const char* stateName() const {
    return state == State::Armed ? "ARMED_BENCH" :
           state == State::TimedOut ? "TIMED_OUT" : "DISARMED";
  }

  void tick(uint32_t now) {
    // Watchdog takes priority over integrating a pending target.
    if (state == State::Armed && uint32_t(now - lastAccepted_) >= 500) {
      stop(State::TimedOut);
    }
    const uint32_t elapsed = now - lastTick_;
    if (elapsed < 20) return;
    lastTick_ = now;
    if (state != State::Armed || elapsed > 100) return;
    const int step = int(50 * elapsed / 1000); // <= 5 degrees/s, virtual only
    pan = approach(pan, targetPan, step);
    tilt = approach(tilt, targetTilt, step);
  }

  void status(char* out, size_t capacity) const {
    snprintf(out, capacity, "STATUS,%s,%d,%d,%d,%d,%lu,OUTPUT_DISABLED",
             stateName(), pan, tilt, targetPan, targetTilt,
             static_cast<unsigned long>(sequence));
  }

  void command(const char* line, uint32_t now, char* out, size_t capacity) {
    tick(now);
    if (strcmp(line, "HELLO") == 0) {
      reply(out, capacity, "HELLO,UAVTRACK_BENCH,0.1.0,OUTPUT_DISABLED"); return;
    }
    if (strcmp(line, "PING") == 0) {
      reply(out, capacity, "PONG,OUTPUT_DISABLED"); return;
    }
    if (strcmp(line, "STATUS") == 0) { status(out, capacity); return; }
    if (strcmp(line, "DISARM") == 0) {
      stop(State::Disarmed);
      reply(out, capacity, "ACK,DISARM,OUTPUT_DISABLED"); return;
    }

    char copy[96];
    if (strlen(line) >= sizeof(copy)) { error(out, capacity, "LENGTH"); return; }
    strcpy(copy, line);
    char* fields[4] = {copy, nullptr, nullptr, nullptr};
    size_t count = 1;
    for (char* p = copy; *p; ++p) {
      if (*p != ',') continue;
      if (count == 4) { error(out, capacity, "FORMAT"); return; }
      *p = 0;
      fields[count++] = p + 1;
    }
    const bool arm = strcmp(fields[0], "ARM_BENCH") == 0;
    const bool set = strcmp(fields[0], "SET") == 0;
    if ((!arm && !set) || count != (arm ? 2u : 4u)) {
      error(out, capacity, "FORMAT"); return;
    }
    uint32_t seq = 0, p = 0, t = 0;
    if (!number(fields[1], seq) || (set &&
        (!number(fields[2], p) || !number(fields[3], t)))) {
      error(out, capacity, "NUMBER"); return;
    }
    if (hasSequence && seq <= sequence) { error(out, capacity, "SEQUENCE"); return; }
    if (set && (p < 850 || p > 950 || t < 850 || t > 950)) {
      error(out, capacity, "RANGE"); return;
    }
    if ((arm && state == State::Armed) || (set && state != State::Armed)) {
      error(out, capacity, "STATE"); return;
    }
    sequence = seq;
    hasSequence = true;
    lastAccepted_ = now;
    if (arm) {
      state = State::Armed;
      lastTick_ = now;
    } else {
      targetPan = int(p); targetTilt = int(t);
    }
    snprintf(out, capacity, "ACK,%lu,%s,SIM_ONLY,OUTPUT_DISABLED",
             static_cast<unsigned long>(seq), arm ? "ARM_BENCH" : "SET");
  }

 private:
  uint32_t lastAccepted_ = 0, lastTick_ = 0;
  static int approach(int current, int target, int step) {
    if (target > current) return target - current > step ? current + step : target;
    return current - target > step ? current - step : target;
  }
  void stop(State next) {
    state = next; targetPan = pan; targetTilt = tilt;
  }
  static bool number(const char* s, uint32_t& result) {
    if (!s || !*s) return false;
    result = 0;
    for (; *s; ++s) {
      if (*s < '0' || *s > '9') return false;
      const uint32_t digit = uint32_t(*s - '0');
      if (result > (UINT32_MAX - digit) / 10) return false;
      result = result * 10 + digit;
    }
    return true;
  }
  static void reply(char* out, size_t capacity, const char* text) {
    snprintf(out, capacity, "%s", text);
  }
  static void error(char* out, size_t capacity, const char* reason) {
    snprintf(out, capacity, "ERR,%s,OUTPUT_DISABLED", reason);
  }
};

// Entire line must arrive within 200 ms. Bad/stale lines drain through LF.
class LineReader {
 public:
  enum class Event { None, Ready, Rejected };
  void expire(uint32_t now) {
    if (used_ && uint32_t(now - started_) >= 200) bad_ = true;
  }
  Event feed(char c, uint32_t now) {
    expire(now);
    if (c == '\n') {
      const bool rejected = bad_;
      if (used_ && buffer_[used_ - 1] == '\r') --used_;
      buffer_[used_] = 0;
      used_ = 0; bad_ = false;
      return rejected ? Event::Rejected : Event::Ready;
    }
    if (bad_) return Event::None;
    if (used_ == 0) started_ = now;
    if (used_ >= sizeof(buffer_) - 1 ||
        (c != '\r' && (static_cast<unsigned char>(c) < 32 ||
                        static_cast<unsigned char>(c) > 126))) {
      bad_ = true; return Event::None;
    }
    buffer_[used_++] = c;
    return Event::None;
  }
  const char* line() const { return buffer_; }
 private:
  char buffer_[96] = {};
  size_t used_ = 0;
  bool bad_ = false;
  uint32_t started_ = 0;
};
} // namespace bench
