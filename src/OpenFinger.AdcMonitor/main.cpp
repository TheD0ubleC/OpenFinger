#include "common/AdcReceiver.h"
#include "common/Config.h"
#include "common/FingerFilter.h"
#include "openfinger/OpenFingerVersion.h"

#include <conio.h>

#include <chrono>
#include <iomanip>
#include <iostream>
#include <thread>
#include <vector>

namespace
{

void PrintHelp()
{
    std::cout << "OpenFinger ADC monitor " << OPENFINGER_VERSION << " / protocol v" << OPENFINGER_PROTOCOL_VERSION << "\n"
              << "Commands: c = calibrate right index, r = reset right index, d = cycle right index direction, q = quit\n\n";
}

void PrintHandStatus(const openfinger::HandRuntimeState& hand, const openfinger::ConfigStore& config_store)
{
    const auto& thumb = hand.fingers[0];
    const auto& index = hand.fingers[1];
    const auto& middle = hand.fingers[2];
    const auto& ring = hand.fingers[3];
    const auto& pinky = hand.fingers[4];

    std::cout << std::string(openfinger::ToString(hand.side))
              << " thumb=" << std::fixed << std::setprecision(3) << thumb.bend_smoothed
              << " index=" << index.bend_smoothed
              << " middle=" << middle.bend_smoothed
              << " ring=" << ring.bend_smoothed
              << " pinky=" << pinky.bend_smoothed
              << " index_raw=" << index.raw
              << " index_center=" << index.center_raw
              << " index_direction=" << openfinger::DirectionLabel(index.direction)
              << " fps=" << std::fixed << std::setprecision(1) << hand.packet_fps
              << " stale=" << (hand.stale ? "yes" : "no")
              << " config=" << config_store.path().string()
              << "\n";
}

} // namespace

int main()
{
    using clock = std::chrono::steady_clock;

    std::cout.setf(std::ios::unitbuf);
    std::cerr.setf(std::ios::unitbuf);

    openfinger::ConfigStore config_store;
    std::string error;
    if (!config_store.LoadOrCreate(&error))
    {
        std::cerr << "Failed to load config: " << error << "\n";
        return 1;
    }

    openfinger::FingerFilter filter(config_store.config());
    openfinger::AdcReceiver receiver;
    if (!receiver.Start(static_cast<std::uint16_t>(config_store.config().runtime.device_udp_port), &error))
    {
        std::cerr << "Failed to start UDP receiver: " << error << "\n";
        return 1;
    }

    PrintHelp();
    std::cout << "Listening on UDP port " << config_store.config().runtime.device_udp_port << "\n";
    std::cout << "Config file: " << config_store.path().string() << "\n\n";

    std::vector<openfinger::ReceivedAdcPacket> packets;
    auto last_idle_status = clock::now();
    bool running = true;

    while (running)
    {
        packets.clear();
        receiver.DrainPackets(&packets);

        if (!packets.empty())
        {
            for (const auto& packet : packets)
            {
                const bool config_changed = filter.ProcessPacket(openfinger::HandSide::Right, packet);
                config_store.mutable_config() = filter.config();
                if (config_changed)
                {
                    std::string save_error;
                    if (!config_store.Save(&save_error))
                    {
                        std::cerr << "Failed to save config: " << save_error << "\n";
                    }
                    else
                    {
                        const auto& right_index = filter.hand_state(openfinger::HandSide::Right).fingers[1];
                        std::cout << "[auto] direction resolved to "
                                  << openfinger::DirectionLabel(right_index.direction)
                                  << " and saved\n";
                    }
                }

                std::cout << packet.raw_line << " from " << packet.source_endpoint << " -> ";
                PrintHandStatus(filter.hand_state(openfinger::HandSide::Right), config_store);
                last_idle_status = clock::now();
            }
        }
        else
        {
            filter.Tick(clock::now());
            if ((clock::now() - last_idle_status) > std::chrono::milliseconds(500))
            {
                PrintHandStatus(filter.hand_state(openfinger::HandSide::Right), config_store);
                last_idle_status = clock::now();
            }
        }

        while (_kbhit())
        {
            const int key = _getch();
            if (key == 'q' || key == 'Q')
            {
                running = false;
                break;
            }

            bool changed = false;
            if (key == 'c' || key == 'C')
            {
                changed = filter.CalibrateCenterFromCurrentRaw(openfinger::HandSide::Right, openfinger::FingerName::Index);
                if (!changed)
                {
                    std::cout << "[cmd] calibrate ignored: no valid sample yet\n";
                }
                else
                {
                    std::cout << "[cmd] calibrated right index\n";
                }
            }
            else if (key == 'r' || key == 'R')
            {
                changed = filter.ResetCenter(openfinger::HandSide::Right, openfinger::FingerName::Index);
                std::cout << "[cmd] reset right index center\n";
            }
            else if (key == 'd' || key == 'D')
            {
                changed = filter.CycleDirection(openfinger::HandSide::Right, openfinger::FingerName::Index);
                std::cout << "[cmd] direction updated\n";
            }

            if (changed)
            {
                config_store.mutable_config() = filter.config();
                std::string save_error;
                if (!config_store.Save(&save_error))
                {
                    std::cerr << "Failed to save config: " << save_error << "\n";
                }
                else
                {
                    PrintHandStatus(filter.hand_state(openfinger::HandSide::Right), config_store);
                }
            }
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(16));
    }

    receiver.Stop();
    return 0;
}
