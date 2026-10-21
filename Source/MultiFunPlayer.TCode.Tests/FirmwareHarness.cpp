// Compile against the actual CTW checkout, not a reimplementation:
// g++ -std=c++17 -O2 -I <ctw>/include FirmwareHarness.cpp -o /tmp/mfp-p4-queue
#include "tcode_queue.hpp"
#include <chrono>
#include <iostream>
#include <memory>

int main() {
    auto queue = std::make_unique<tcode_v04::Queue>();
    queue->boot = 99;
    tcode_v04::State state;
    state.p.fill(.5f);
    const auto origin = std::chrono::steady_clock::now();
    std::string line;
    while (std::getline(std::cin, line)) {
        const auto now = std::chrono::duration_cast<std::chrono::microseconds>(
            std::chrono::steady_clock::now() - origin).count() + 1000000;
        std::array<tcode_v04::Envelope, 2> envelope;
        queue->sample(now, state, envelope);
        // This harness tests protocol state only. NativeP4Tests exercises the
        // real 1 kHz bounded braking/settling implementation.
        if(queue->isPaused()) {state=queue->pausePose();queue->updatePauseState(state);}
        std::vector<uint64_t> fields;
        const bool stop = tcode_v04::decode(line, fields) && fields[0] == 5;
        std::cout << queue->handle(line + "\n", now, state, true) << std::flush;
        if (stop) { state.v.fill(0); state.a.fill(0); state.j.fill(0); }
    }
}
