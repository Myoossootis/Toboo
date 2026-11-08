#pragma once
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <math.h>

namespace single_servo {
constexpr int kMinAngle[] = {30, 0, 30};
constexpr int kMaxAngle[] = {175, 180, 90};
constexpr int kPairMaxAngle[] = {175, 180};
constexpr int kFirstMinAngle[] = {80, 45, 60};

inline bool validServoFrame(const char* frame, size_t length) {
  if (length != 6 || frame[0] != '$' || frame[1] < 'A' || frame[1] > 'C' || frame[5] != '#') return false;
  const int axis = frame[1] - 'A';
  for (size_t i = 2; i < 5; ++i) {
    if (frame[i] < '0' || frame[i] > '9') return false;
  }
  const int angle = (frame[2] - '0') * 100 + (frame[3] - '0') * 10 + (frame[4] - '0');
  return angle >= kMinAngle[axis] && angle <= kMaxAngle[axis];
}

// Output completion means a UART frame was accepted, NEVER measured shaft position.
struct Output {
  virtual ~Output() {}
  virtual bool enable(bool enabled) = 0;
  virtual bool send(const char* frame, size_t length) = 0;
};
enum class State { Disarmed, Armed, TimedOut, Fault };

class Controller {
 public:
  explicit Controller(Output& output) : output_(output) {}
  State state = State::Disarmed;
  uint32_t sequence = 0, frames = 0;
  bool hasSequence = false;

  void start(uint32_t bootHigh, uint32_t bootLow) {
    output_.enable(false);
    state = State::Disarmed;
    for (auto& position : positions_) position = Position{};
    activeAxis_ = 0; sequence = frames = 0; hasSequence = false;
    dual_ = moving_ = follow_ = false;
    bootHigh_ = bootHigh; bootLow_ = bootLow; epoch_ = 1;
    lastControl_ = lastTick_ = lastSend_ = 0;
    formatSession();
  }
  const char* session() const { return session_; }
  int last(int axis = 0) const { return positions_[axis].last; }
  int target(int axis = 0) const { return positions_[axis].target; }
  const char* stateName() const {
    switch (state) {
      case State::Armed: return follow_ ? "ARMED_FOLLOW" : dual_ ? "ARMED_BOTH" : activeAxis_ == 0 ? "ARMED_S1" : activeAxis_ == 1 ? "ARMED_S2" : "ARMED_S3";
      case State::TimedOut: return "TIMED_OUT";
      case State::Fault: return "FAULT";
      default: return "DISARMED";
    }
  }
  void stop(State next = State::Disarmed) {
    output_.enable(false);
    state = next;
    for (auto& position : positions_) position.target = position.last;
    moving_ = dual_ = follow_ = false;
    ++epoch_; // Invalidates queued commands from the previous enable period.
    formatSession();
  }
  void tick(uint32_t now) {
    if (state == State::Armed && uint32_t(now - lastControl_) >= 500) {
      stop(State::TimedOut);
    }
    const uint32_t elapsed = now - lastTick_;
    lastTick_ = now;
    if (state != State::Armed) return;
    if (follow_) {
      if (elapsed > 100) { stop(State::TimedOut); return; }
      // A heartbeat never authorizes continued travel toward an old visual target.
      if (uint32_t(now - lastFollow_) >= 150) holdFollow();
      if (uint32_t(now - followTick_) >= 20) advanceFollow(now);
      return;
    }
    if (dual_) {
      if (elapsed > 100) { stop(State::TimedOut); return; }
      if (moving_ && uint32_t(now - lastSend_) >= 20) advancePair(now);
      return;
    }
    auto& position = positions_[activeAxis_];
    if (elapsed > 100) {
      position.target = position.last; // Scheduling stall: cancel, do not catch up later.
      return;
    }
    if (position.last >= 0 && position.target != position.last && uint32_t(now - lastSend_) >= 200) {
      emit(position.last + (position.target > position.last ? 1 : -1), now);
    }
  }
  void status(char* out, size_t size, int axis) const {
    snprintf(out, size, "STATUS,%s,%s,%lu,%d,%d,%lu,S%d_ONLY", stateName(), session_,
      static_cast<unsigned long>(sequence), last(axis), target(axis), static_cast<unsigned long>(frames), axis + 1);
  }
  void command(const char* line, uint32_t now, char* out, size_t size) {
    tick(now);
    if (!strcmp(line, "HELLO")) {
      snprintf(out, size, "HELLO,UAVTRACK_PANTILT,0.5.3,UART_CAPABLE,DEFAULT_DISARMED"); return;
    }
    if (!strcmp(line, "STATUS")) { status(out, size, 0); return; }
    if (!strcmp(line, "STATUS_S2")) { status(out, size, 1); return; }
    if (!strcmp(line, "STATUS_S3")) { status(out, size, 2); return; }
    if (!strcmp(line, "MOTION")) {
      snprintf(out, size, "MOTION,%s", moving_ ? "MOVING" : "IDLE"); return;
    }
    if (!strcmp(line, "PING")) { snprintf(out, size, "PONG,S1_S2"); return; }
    if (!strcmp(line, "DISARM")) {
      stop(); snprintf(out, size, "ACK,DISARM,TX_DISABLED"); return;
    }
    if (!strncmp(line, "ARM_FOLLOW,", 11) || !strncmp(line, "FOLLOW_BOTH,", 12) ||
        !strncmp(line, "HOLD_BOTH,", 10) || !strncmp(line, "KEEP_FOLLOW,", 12)) {
      followCommand(line, now, out, size); return;
    }
    if (!strncmp(line, "ARM_BOTH,", 9) || !strncmp(line, "KEEP_BOTH,", 10) ||
        !strncmp(line, "INIT_BOTH,", 10) || !strncmp(line, "MOVE_BOTH,", 10)) {
      pairCommand(line, now, out, size); return;
    }
    char copy[96];
    if (strlen(line) >= sizeof(copy)) { error(out, size, "LENGTH"); return; }
    strcpy(copy, line);
    char* fields[4] = {copy, nullptr, nullptr, nullptr};
    size_t count = 1;
    for (char* p = copy; *p; ++p) {
      if (*p != ',') continue;
      if (count == 4) { error(out, size, "FORMAT"); return; }
      *p = 0; fields[count++] = p + 1;
    }
    char* suffix = strrchr(fields[0], '_');
    if (!suffix || (strcmp(suffix, "_S1") && strcmp(suffix, "_S2") && strcmp(suffix, "_S3"))) {
      error(out, size, "FORMAT"); return;
    }
    const int axis = suffix[2] - '1';
    *suffix = 0;
    const bool arm = !strcmp(fields[0], "ARM");
    const bool center = !strcmp(fields[0], "CENTER");
    const bool move = !strcmp(fields[0], "MOVE");
    const bool keep = !strcmp(fields[0], "KEEP");
    const bool hasAngle = move || (center && count == 4);
    if ((!arm && !center && !move && !keep) || count != (hasAngle ? 4u : 3u)) {
      error(out, size, "FORMAT"); return;
    }
    if (strcmp(fields[1], session_)) { error(out, size, "SESSION"); return; }
    uint32_t seq = 0, angle = 90;
    if (!number(fields[2], seq) || (hasAngle && !number(fields[3], angle))) {
      error(out, size, "NUMBER"); return;
    }
    if (hasSequence && seq <= sequence) { error(out, size, "SEQUENCE"); return; }
    if ((move || center) && (angle < uint32_t(kMinAngle[axis]) || angle > uint32_t(kMaxAngle[axis]))) {
      error(out, size, "RANGE"); return;
    }
    if (center && angle < uint32_t(kFirstMinAngle[axis])) {
      error(out, size, axis == 0 ? "FIRST_PAN_RANGE" : axis == 1 ? "FIRST_TILT_RANGE" : "FIRST_S3_RANGE"); return;
    }
    if ((arm && state == State::Armed) || (!arm && state != State::Armed)) {
      error(out, size, "STATE"); return;
    }
    if (!arm && (dual_ || follow_ || axis != activeAxis_)) { error(out, size, "AXIS"); return; }
    auto& position = positions_[axis];
    if (axis == 2 && center && angle != 60) { error(out, size, "FIRST_S3_RANGE"); return; }
    if (axis == 1 && center && angle != 45) { error(out, size, "FIRST_TILT_RANGE"); return; }
    // Beyond the observed 45-90 interval, calibration must explore one request degree at a time.
    if (axis == 1 && move && (angle < 45 || angle > 90) && position.last >= 0 &&
        (int(angle) - position.last > 1 || position.last - int(angle) > 1)) {
      error(out, size, "CALIBRATION_STEP"); return;
    }
    if (axis == 1 && move && position.last > kMaxAngle[1]) { error(out, size, "PATH"); return; }
    if ((center && position.last >= 0) || (move && position.last < 0)) {
      error(out, size, "REFERENCE"); return;
    }
    // Failed requests above do NOT consume a sequence or feed the watchdog.
    sequence = seq; hasSequence = true; lastControl_ = now;
    if (arm) {
      dual_ = false;
      activeAxis_ = axis;
      if (!output_.enable(true)) { stop(State::Fault); error(out, size, "UART"); return; }
      state = State::Armed; lastTick_ = now; position.target = position.last;
    } else if (center) {
      // Explicit first request: unknown starting shaft position, NOT slew limited.
      if (!emit(int(angle), now)) { error(out, size, "UART"); return; }
      position.target = position.last;
    } else if (move) {
      position.target = int(angle);
    }
    *suffix = '_';
    snprintf(out, size, "ACK,%lu,%s,REQUEST_ONLY", static_cast<unsigned long>(seq), fields[0]);
  }
 private:
  Output& output_;
  struct Position { int last = -1, target = -1; };
  Position positions_[3]; // All references are unknown after EVERY MCU restart.
  int activeAxis_ = 0;
  bool dual_ = false, moving_ = false, follow_ = false;
  double followPosition_[2] = {}, followVelocity_[2] = {}, followTarget_[2] = {};
  int followMin_[2] = {}, followMax_[2] = {};
  uint32_t lastFollow_ = 0, followTick_ = 0;
  int from_[2] = {};
  uint32_t motionStart_ = 0, duration_ = 0;
  uint32_t bootHigh_ = 0, bootLow_ = 0, epoch_ = 0;
  uint32_t lastControl_ = 0, lastTick_ = 0, lastSend_ = 0;
  char session_[26] = {};
  void holdFollow() {
    for (int axis = 0; axis < 2; ++axis) {
      followPosition_[axis] = followTarget_[axis] = positions_[axis].last;
      followVelocity_[axis] = 0;
      positions_[axis].target = positions_[axis].last;
    }
    moving_ = false;
  }
  void advanceFollow(uint32_t now) {
    const double dt = double(uint32_t(now - followTick_)) / 1000;
    followTick_ = now; moving_ = false;
    for (int axis = 0; axis < 2; ++axis) {
      const double error = followTarget_[axis] - followPosition_[axis];
      const double desired = fmax(-20.0, fmin(20.0, error * 6.0));
      const double oldVelocity = followVelocity_[axis];
      double velocity = oldVelocity + fmax(-80 * dt, fmin(80 * dt, desired - oldVelocity));
      double next = followPosition_[axis] + (oldVelocity + velocity) * 0.5 * dt;
      // Limits and explicit holds take priority over the nominal acceleration ramp.
      if (next < followMin_[axis] || next > followMax_[axis]) velocity = 0;
      next = fmax(double(followMin_[axis]), fmin(double(followMax_[axis]), next));
      followPosition_[axis] = next; followVelocity_[axis] = velocity;
      const int angle = int(lround(next));
      if (angle != positions_[axis].last && !emitAxis(axis, angle, now)) return;
      moving_ |= fabs(followTarget_[axis] - next) > 0.02 || fabs(velocity) > 0.02;
    }
  }
  void followCommand(const char* line, uint32_t now, char* out, size_t size) {
    char copy[96];
    if (strlen(line) >= sizeof(copy)) { error(out, size, "LENGTH"); return; }
    strcpy(copy, line);
    char* fields[7] = {copy, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr};
    size_t count = 1;
    for (char* p = copy; *p; ++p) {
      if (*p != ',') continue;
      if (count == 7) { error(out, size, "FORMAT"); return; }
      *p = 0; fields[count++] = p + 1;
    }
    const bool arm = !strcmp(fields[0], "ARM_FOLLOW");
    const bool target = !strcmp(fields[0], "FOLLOW_BOTH");
    const bool hold = !strcmp(fields[0], "HOLD_BOTH");
    const bool keep = !strcmp(fields[0], "KEEP_FOLLOW");
    if ((!arm && !target && !hold && !keep) || count != (arm ? 7u : target ? 5u : 3u)) {
      error(out, size, "FORMAT"); return;
    }
    if (strcmp(fields[1], session_)) { error(out, size, "SESSION"); return; }
    uint32_t seq = 0, values[4] = {};
    if (!number(fields[2], seq)) { error(out, size, "NUMBER"); return; }
    if (hasSequence && seq <= sequence) { error(out, size, "SEQUENCE"); return; }
    if ((arm && state != State::Disarmed) || (!arm && (state != State::Armed || !follow_))) {
      error(out, size, "STATE"); return;
    }
    for (size_t i = 3; i < count; ++i) {
      if (!number(fields[i], values[i - 3])) { error(out, size, "NUMBER"); return; }
    }
    for (int axis = 0; axis < 2; ++axis) {
      if (arm) {
        if (values[axis * 2] < uint32_t(kMinAngle[axis]) || values[axis * 2 + 1] > uint32_t(kPairMaxAngle[axis]) ||
            values[axis * 2] >= values[axis * 2 + 1]) { error(out, size, "RANGE"); return; }
        if (positions_[axis].last < int(values[axis * 2]) || positions_[axis].last > int(values[axis * 2 + 1])) {
          error(out, size, "REFERENCE"); return;
        }
      } else if (target && (values[axis] < uint32_t(followMin_[axis] * 100) ||
                 values[axis] > uint32_t(followMax_[axis] * 100))) { error(out, size, "RANGE"); return; }
    }
    sequence = seq; hasSequence = true; lastControl_ = now;
    if (arm) {
      if (!output_.enable(true)) { stop(State::Fault); error(out, size, "UART"); return; }
      dual_ = false; follow_ = true; state = State::Armed; lastTick_ = followTick_ = lastFollow_ = now;
      for (int axis = 0; axis < 2; ++axis) {
        followMin_[axis] = int(values[axis * 2]); followMax_[axis] = int(values[axis * 2 + 1]);
      }
      holdFollow(); // No initialization or centering on entry; both references must already exist.
    } else if (target) {
      lastFollow_ = now;
      for (int axis = 0; axis < 2; ++axis) {
        followTarget_[axis] = double(values[axis]) / 100;
        positions_[axis].target = int(lround(followTarget_[axis]));
      }
    } else if (hold) { holdFollow(); }
    snprintf(out, size, "ACK,%lu,%s,REQUEST_ONLY", static_cast<unsigned long>(seq), fields[0]);
  }
  bool emitAxis(int axis, int angle, uint32_t now) {
    const int oldAxis = activeAxis_;
    activeAxis_ = axis;
    const bool ok = emit(angle, now);
    activeAxis_ = oldAxis;
    return ok;
  }
  void advancePair(uint32_t now) {
    const uint32_t elapsed = now - motionStart_;
    const double u = elapsed >= duration_ ? 1.0 : double(elapsed) / duration_;
    const double progress = u * u * u * (10.0 + u * (-15.0 + 6.0 * u));
    int angles[2];
    for (int axis = 0; axis < 2; ++axis)
      angles[axis] = int(lround(from_[axis] + (positions_[axis].target - from_[axis]) * progress));
    for (int axis = 0; axis < 2; ++axis) {
      if (angles[axis] != positions_[axis].last && !emitAxis(axis, angles[axis], now)) return;
    }
    lastSend_ = now;
    if (elapsed >= duration_) moving_ = false;
  }
  void pairCommand(const char* line, uint32_t now, char* out, size_t size) {
    char copy[96];
    if (strlen(line) >= sizeof(copy)) { error(out, size, "LENGTH"); return; }
    strcpy(copy, line);
    char* fields[5] = {copy, nullptr, nullptr, nullptr, nullptr};
    size_t count = 1;
    for (char* p = copy; *p; ++p) {
      if (*p != ',') continue;
      if (count == 5) { error(out, size, "FORMAT"); return; }
      *p = 0; fields[count++] = p + 1;
    }
    const bool arm = !strcmp(fields[0], "ARM_BOTH");
    const bool keep = !strcmp(fields[0], "KEEP_BOTH");
    const bool init = !strcmp(fields[0], "INIT_BOTH");
    if (count != ((arm || keep) ? 3u : 5u)) { error(out, size, "FORMAT"); return; }
    if (strcmp(fields[1], session_)) { error(out, size, "SESSION"); return; }
    uint32_t seq = 0, angles[2] = {};
    if (!number(fields[2], seq)) { error(out, size, "NUMBER"); return; }
    if (hasSequence && seq <= sequence) { error(out, size, "SEQUENCE"); return; }
    if ((arm && state == State::Armed) || (!arm && (state != State::Armed || !dual_))) {
      error(out, size, "STATE"); return;
    }
    if (arm) {
      for (int axis = 0; axis < 2; ++axis) {
        if (positions_[axis].last >= 0 && (positions_[axis].last < kMinAngle[axis] ||
            positions_[axis].last > kPairMaxAngle[axis])) { error(out, size, "REFERENCE"); return; }
      }
    }
    if (!arm && !keep) {
      for (int axis = 0; axis < 2; ++axis) {
        if (!number(fields[axis + 3], angles[axis])) { error(out, size, "NUMBER"); return; }
        if (angles[axis] < uint32_t(kMinAngle[axis]) || angles[axis] > uint32_t(kPairMaxAngle[axis])) {
          error(out, size, "RANGE"); return;
        }
        if (init ? positions_[axis].last >= 0 : positions_[axis].last < 0) {
          error(out, size, "REFERENCE"); return;
        }
      }
      if (moving_) { error(out, size, "BUSY"); return; }
    }
    sequence = seq; hasSequence = true; lastControl_ = now;
    if (arm) {
      if (!output_.enable(true)) { stop(State::Fault); error(out, size, "UART"); return; }
      dual_ = true; state = State::Armed; lastTick_ = now;
      for (auto& position : positions_) position.target = position.last;
    } else if (init) {
      // Explicit initialization only: actual shaft positions are unknown.
      if (!emitAxis(0, int(angles[0]), now) || !emitAxis(1, int(angles[1]), now)) {
        error(out, size, "UART"); return;
      }
      for (auto& position : positions_) position.target = position.last;
    } else if (!keep) {
      int distance = 0;
      for (int axis = 0; axis < 2; ++axis) {
        from_[axis] = positions_[axis].last;
        positions_[axis].target = int(angles[axis]);
        const int delta = int(angles[axis]) - from_[axis];
        const int magnitude = delta < 0 ? -delta : delta;
        if (magnitude > distance) distance = magnitude;
      }
      // Quintic smoothstep peaks at 1.875. Bound both axes to 30 degrees/s.
      duration_ = uint32_t((distance * 125 + 39) / 40) * 20;
      if (duration_ < 400) duration_ = 400;
      motionStart_ = lastSend_ = now; moving_ = distance > 0;
    }
    snprintf(out, size, "ACK,%lu,%s,REQUEST_ONLY", static_cast<unsigned long>(seq), fields[0]);
  }
  void formatSession() {
    snprintf(session_, sizeof(session_), "%08lx%08lx-%08lx",
      static_cast<unsigned long>(bootHigh_), static_cast<unsigned long>(bootLow_),
      static_cast<unsigned long>(epoch_));
  }
  bool emit(int angle, uint32_t now) {
    char frame[8];
    const int length = snprintf(frame, sizeof(frame), "$%c%03d#", 'A' + activeAxis_, angle);
    if (length != 6 || !validServoFrame(frame, 6) || !output_.send(frame, 6)) { stop(State::Fault); return false; }
    positions_[activeAxis_].last = angle; lastSend_ = now; ++frames;
    return true;
  }
  static bool number(const char* text, uint32_t& result) {
    if (!text || !*text) return false;
    result = 0;
    for (; *text; ++text) {
      if (*text < '0' || *text > '9') return false;
      const uint32_t digit = uint32_t(*text - '0');
      if (result > (UINT32_MAX - digit) / 10) return false;
      result = result * 10 + digit;
    }
    return true;
  }
  static void error(char* out, size_t size, const char* reason) {
    snprintf(out, size, "ERR,%s,S1_S2", reason);
  }
};

// Standalone sketch: retain the bench reader's bounded, drain-to-LF behavior.
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
      if (used_ && data_[used_ - 1] == '\r') --used_;
      data_[used_] = 0; used_ = 0; bad_ = false;
      return rejected ? Event::Rejected : Event::Ready;
    }
    if (bad_) return Event::None;
    if (!used_) started_ = now;
    if (used_ >= sizeof(data_) - 1 || (c != '\r' &&
        (static_cast<unsigned char>(c) < 32 || static_cast<unsigned char>(c) > 126))) {
      bad_ = true; return Event::None;
    }
    data_[used_++] = c;
    return Event::None;
  }
  const char* line() const { return data_; }
 private:
  char data_[96] = {};
  size_t used_ = 0;
  uint32_t started_ = 0;
  bool bad_ = false;
};
} // namespace single_servo
