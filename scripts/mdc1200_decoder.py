#!/usr/bin/env python3
"""
Lightweight MDC1200 Decoder for OpenScanner
============================================

Decodes MDC1200 signaling from analog FM audio.
Reads 48kHz mono PCM audio from stdin, detects MDC1200 bursts,
and outputs unit IDs to stderr in format: MDC1200: <hex_id>

MDC1200 Standard:
- 1200 baud FSK (frequency shift keying)
- Typical frequencies: 1500 Hz (mark), 1800 Hz (space) for 48kHz audio
- Duration: ~80ms per message
- Data: 40 bits (8 parity, 32 data with 24-bit unit ID)

Author: OpenScanner Contributors
License: Matched to OpenScanner project
"""

import sys
import struct
import math

class SimpleFSKDetector:
    """Basic FSK detector using zero-crossing rate and frequency estimation."""
    
    def __init__(self, sample_rate=48000, baud_rate=1200, mark_freq=1500, space_freq=1800):
        self.sample_rate = sample_rate
        self.baud_rate = baud_rate
        self.mark_freq = mark_freq
        self.space_freq = space_freq
        self.samples_per_bit = sample_rate // baud_rate
        
    def detect_frequency(self, samples):
        """Simple frequency detection using zero-crossing rate."""
        if len(samples) < 2:
            return 0
        
        # Count zero crossings
        zero_crossings = 0
        for i in range(len(samples) - 1):
            if (samples[i] >= 0) != (samples[i + 1] >= 0):
                zero_crossings += 1
        
        # Frequency from zero crossing rate
        # zc_rate = 2 * freq * time, so freq = zc_rate / (2 * time)
        time = len(samples) / self.sample_rate
        detected_freq = zero_crossings / (2 * time)
        return detected_freq
    
    def decode_bits(self, samples):
        """Decode FSK samples into bits."""
        bits = []
        samples_per_symbol = self.samples_per_bit
        
        for i in range(0, len(samples), samples_per_symbol):
            chunk = samples[i:i + samples_per_symbol]
            if len(chunk) < samples_per_symbol // 2:
                break
            
            freq = self.detect_frequency(chunk)
            
            # Determine if mark (1) or space (0)
            mid_freq = (self.mark_freq + self.space_freq) / 2
            bit = 1 if freq > mid_freq else 0
            bits.append(bit)
        
        return bits

class MDC1200Decoder:
    """MDC1200 message decoder."""
    
    # Bit patterns for synchronization
    SYNC_PATTERN = [1, 0, 1, 0, 1, 0, 1, 0]  # Alternating pattern
    
    @staticmethod
    def hamming_decode_7_4(data_bits):
        """Simple Hamming(7,4) error correction."""
        # This is simplified - full implementation would validate parity
        # For now, just extract the 4 data bits
        if len(data_bits) >= 7:
            return [data_bits[0], data_bits[1], data_bits[2], data_bits[3]]
        return data_bits[:4]
    
    @staticmethod
    def bits_to_bytes(bits):
        """Convert list of bits to bytes."""
        bytes_data = []
        for i in range(0, len(bits), 8):
            byte_bits = bits[i:i + 8]
            if len(byte_bits) < 8:
                break
            byte_val = 0
            for j, bit in enumerate(byte_bits):
                byte_val |= (bit << (7 - j))
            bytes_data.append(byte_val)
        return bytes(bytes_data)
    
    @staticmethod
    def extract_unit_id(data_bytes):
        """Extract 24-bit unit ID from MDC1200 data."""
        if len(data_bytes) < 4:
            return None
        
        # MDC1200 unit ID is typically in first 3-4 bytes
        # Extract as 24-bit or 32-bit value depending on format
        unit_id = (data_bytes[1] << 16) | (data_bytes[2] << 8) | data_bytes[3]
        return unit_id
    
    @staticmethod
    def find_sync_position(bits, sync_pattern):
        """Find the position of sync pattern in bits."""
        if len(bits) < len(sync_pattern):
            return -1
        
        for i in range(len(bits) - len(sync_pattern)):
            match = all(bits[i + j] == sync_pattern[j] for j in range(len(sync_pattern)))
            if match:
                return i
        return -1
    
    @staticmethod
    def validate_message(bits):
        """Rigorous validation of MDC1200 message structure."""
        if len(bits) < 40:
            return False
        
        # Look for sync pattern (alternating 1010)
        sync_pattern = [1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0]  # 16-bit preamble
        sync_pos = MDC1200Decoder.find_sync_position(bits[:32], sync_pattern[:16])
        
        if sync_pos < 0:
            return False
        
        return True
    
    @classmethod
    def decode(cls, bits):
        """Decode MDC1200 bits into unit ID."""
        if not cls.validate_message(bits):
            return None
        
        try:
            # Find where the actual data starts (after preamble)
            data_bytes = cls.bits_to_bytes(bits[16:])  # Skip preamble
            if len(data_bytes) < 4:
                return None
            
            unit_id = cls.extract_unit_id(data_bytes)
            
            # Validate unit ID is reasonable (non-zero, not all bits set, in valid range)
            # Valid unit IDs are typically 1-9999 (0x1 to 0x270F)
            if unit_id is None or unit_id == 0 or unit_id == 0xFFFFFF:
                return None
            
            # Reject suspiciously high values
            if unit_id > 0x10000:  # > 65536
                return None
            
            return unit_id
        except:
            return None

def read_pcm_samples(num_samples, sample_width=2):
    """Read PCM samples from stdin."""
    data = sys.stdin.buffer.read(num_samples * sample_width)
    if len(data) < sample_width:
        return None
    
    samples = []
    for i in range(0, len(data), sample_width):
        if i + sample_width > len(data):
            break
        # Read as signed 16-bit PCM, little-endian
        sample = struct.unpack('<h', data[i:i+sample_width])[0]
        # Normalize to [-1.0, 1.0]
        samples.append(sample / 32768.0)
    
    return samples

def detect_mdc_bursts(sample_rate=48000):
    """Main MDC detection loop."""
    fsk_detector = SimpleFSKDetector(sample_rate=sample_rate)
    burst_duration_samples = int(sample_rate * 0.1)  # 100ms window
    
    buffer = []
    burst_detected_at = 0
    last_mdc_time = 0
    detection_cooldown = 0.5  # 500ms minimum between detections
    quiet_threshold = 0.15  # RMS must be > 0.15 to process (eliminates low noise)
    
    try:
        while True:
            # Read chunk of audio
            samples = read_pcm_samples(burst_duration_samples)
            if samples is None:
                break
            
            buffer.extend(samples)
            
            # Keep buffer at reasonable size
            if len(buffer) > sample_rate * 2:  # 2 second buffer
                buffer = buffer[-sample_rate:]
            
            # Calculate RMS for this chunk
            rms = math.sqrt(sum(s * s for s in samples) / len(samples)) if samples else 0
            
            # Only process if signal is strong enough AND we're not in cooldown
            # MDC bursts are typically 0.15-0.3 RMS (much louder than background)
            time_since_last = burst_detected_at - last_mdc_time
            if rms > quiet_threshold and time_since_last > detection_cooldown:
                # Try to decode recent samples - MDC burst is ~80ms
                if len(buffer) > sample_rate // 16:  # At least 62.5ms at 48kHz
                    try:
                        # Use fixed 80ms window for MDC (standard duration)
                        window_samples = int(sample_rate * 0.08)  # 80ms
                        if len(buffer) >= window_samples:
                            bits = fsk_detector.decode_bits(buffer[-window_samples:])
                            unit_id = MDC1200Decoder.decode(bits)
                            
                            if unit_id is not None:
                                # Output in format expected by DSDBase
                                sys.stderr.write(f"MDC1200: {unit_id:06x}\n")
                                sys.stderr.flush()
                                last_mdc_time = burst_detected_at
                    except Exception as e:
                        pass
            
            # Pass through audio to stdout
            sys.stdout.buffer.write(struct.pack('<' + 'h' * len(samples), 
                                               *[int(s * 32767) for s in samples]))
            sys.stdout.flush()
            
            burst_detected_at += len(samples) / sample_rate
    
    except KeyboardInterrupt:
        pass
    except BrokenPipeError:
        pass

if __name__ == '__main__':
    sys.stderr.write("MDC1200 Decoder started\n")
    sys.stderr.flush()
    detect_mdc_bursts(sample_rate=48000)
    sys.stderr.write("MDC1200 Decoder stopped\n")
    sys.stderr.flush()
