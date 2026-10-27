"""Sample Linux amdgpu process residency/eviction gauges alongside device pressure.

GTT allocation is not synonymous with spill. Prefer amd-evicted-vram when the
driver exposes it, and retain both requested and resident VRAM for comparison.
"""
import argparse
import csv
import glob
from pathlib import Path
import time


def parse_fdinfo(text):
    result = {}
    for line in text.splitlines():
        if ":" not in line:
            continue
        key, value = line.split(":", 1)
        result[key] = value.strip()
    return result


def kib(value):
    if value is None:
        return None
    fields = value.split()
    if len(fields) == 1:
        return int(fields[0]) / 1024
    number, unit = fields
    return int(number) * {"KiB": 1, "MiB": 1024, "bytes": 1 / 1024}[unit]


def sample_process(pid):
    clients = {}
    for path in glob.glob(f"/proc/{pid}/fdinfo/*"):
        try:
            data = parse_fdinfo(Path(path).read_text())
        except (FileNotFoundError, PermissionError, ProcessLookupError):
            continue
        if data.get("drm-driver") != "amdgpu":
            continue
        clients[(data.get("drm-pdev"), data.get("drm-client-id"))] = data
    metrics = ("drm-resident-vram", "drm-resident-gtt", "amd-evicted-vram", "amd-requested-vram", "amd-requested-gtt")
    devices = {}
    for (device, _), data in clients.items():
        row = devices.setdefault(device, {key: None for key in metrics})
        for key in metrics:
            value = kib(data.get(key))
            if value is not None:
                row[key] = (row[key] or 0) + value
    return devices


def device_pressure():
    result = {}
    for card in Path("/sys/class/drm").glob("card[0-9]*"):
        if "-" in card.name:
            continue
        device = card / "device"
        try:
            result[device.resolve().name] = {key: int((device / key).read_text()) for key in
                ("gpu_busy_percent", "mem_info_vram_used", "mem_info_vram_total")}
        except (OSError, ValueError):
            continue
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pid", type=int)
    parser.add_argument("--seconds", type=float, default=30)
    parser.add_argument("--interval", type=float, default=1)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if args.seconds <= 0 or args.interval <= 0:
        parser.error("seconds and interval must be positive")
    fields = ["elapsed_s", "pid", "device", "resident_vram_kib", "resident_gtt_kib", "evicted_vram_kib",
              "requested_vram_kib", "requested_gtt_kib", "device_busy_pct", "device_used_bytes", "device_total_bytes"]
    start = time.monotonic()
    with args.out.open("w", newline="") as handle:
        writer = csv.DictWriter(handle, fields)
        writer.writeheader()
        while Path(f"/proc/{args.pid}").exists() and time.monotonic() - start < args.seconds:
            pressure = device_pressure()
            for device, data in sample_process(args.pid).items():
                gpu = pressure.get(device, {})
                writer.writerow(dict(elapsed_s=round(time.monotonic() - start, 3), pid=args.pid, device=device,
                    resident_vram_kib=data["drm-resident-vram"], resident_gtt_kib=data["drm-resident-gtt"],
                    evicted_vram_kib=data["amd-evicted-vram"], requested_vram_kib=data["amd-requested-vram"],
                    requested_gtt_kib=data["amd-requested-gtt"], device_busy_pct=gpu.get("gpu_busy_percent"),
                    device_used_bytes=gpu.get("mem_info_vram_used"), device_total_bytes=gpu.get("mem_info_vram_total")))
            handle.flush()
            time.sleep(min(args.interval, max(0, args.seconds - (time.monotonic() - start))))
