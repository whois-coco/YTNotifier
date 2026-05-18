#!/usr/bin/env python3
"""
アイコンプレースホルダー生成スクリプト
実際の開発時はこのスクリプトを実行するか、専用の.icoファイルを用意してください。
"""
import struct
import zlib
import os

def create_minimal_ico(path, size=32, color=(255, 0, 0)):
    """最小限のICOファイルを生成"""
    # 1x1 BMP データを作成（実際は指定サイズ）
    w, h = size, size
    bmp_data = bytearray()
    
    # BITMAPINFOHEADER (40 bytes)
    bmp_data += struct.pack('<I', 40)   # biSize
    bmp_data += struct.pack('<i', w)    # biWidth
    bmp_data += struct.pack('<i', h*2)  # biHeight (AND mask含む)
    bmp_data += struct.pack('<H', 1)    # biPlanes
    bmp_data += struct.pack('<H', 32)   # biBitCount
    bmp_data += struct.pack('<I', 0)    # biCompression
    bmp_data += struct.pack('<I', w*h*4) # biSizeImage
    bmp_data += struct.pack('<i', 0)    # biXPelsPerMeter
    bmp_data += struct.pack('<i', 0)    # biYPelsPerMeter
    bmp_data += struct.pack('<I', 0)    # biClrUsed
    bmp_data += struct.pack('<I', 0)    # biClrImportant

    # ピクセルデータ (BGRA)
    r, g, b = color
    for y in range(h-1, -1, -1):
        for x in range(w):
            # 角丸の円形マスク
            cx, cy = w//2, h//2
            dist = ((x-cx)**2 + (y-cy)**2) ** 0.5
            if dist < min(w,h)//2 - 1:
                alpha = 255
            elif dist < min(w,h)//2:
                alpha = 128
            else:
                alpha = 0
            bmp_data += bytes([b, g, r, alpha])

    # AND mask (all zeros = fully visible)
    mask_size = ((w + 31) // 32) * 4 * h
    bmp_data += bytes(mask_size)

    # ICO ヘッダー
    ico = bytearray()
    ico += struct.pack('<H', 0)   # Reserved
    ico += struct.pack('<H', 1)   # Type: ICO
    ico += struct.pack('<H', 1)   # Image count
    
    # ICONDIRENTRY
    ico += struct.pack('<B', w if w < 256 else 0)  # Width
    ico += struct.pack('<B', h if h < 256 else 0)  # Height
    ico += struct.pack('<B', 0)   # ColorCount
    ico += struct.pack('<B', 0)   # Reserved
    ico += struct.pack('<H', 1)   # Planes
    ico += struct.pack('<H', 32)  # BitCount
    ico += struct.pack('<I', len(bmp_data))  # BytesInRes
    ico += struct.pack('<I', 22)  # ImageOffset (6 + 16)
    
    ico += bmp_data
    
    with open(path, 'wb') as f:
        f.write(ico)

def create_minimal_png(path, size=64, color=(59, 130, 246), warn=False):
    """最小限のPNGファイルを生成"""
    w, h = size, size
    
    def write_chunk(chunk_type, data):
        chunk = chunk_type + data
        return struct.pack('>I', len(data)) + chunk + struct.pack('>I', zlib.crc32(chunk) & 0xffffffff)
    
    # PNG シグネチャ
    png = b'\x89PNG\r\n\x1a\n'
    
    # IHDR
    ihdr_data = struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)
    png += write_chunk(b'IHDR', ihdr_data)
    
    # IDAT (ピクセルデータ)
    r, g, b_val = color
    raw_data = bytearray()
    cx, cy = w//2, h//2
    for y in range(h):
        raw_data += b'\x00'  # filter type none
        for x in range(w):
            dist = ((x-cx)**2 + (y-cy)**2) ** 0.5
            if warn:
                # 警告アイコン: 黄色三角
                in_triangle = (y > h*0.2) and (abs(x-cx) < (y - h*0.2) * 0.7)
                if in_triangle:
                    raw_data += bytes([255, 200, 0])
                else:
                    raw_data += bytes([59, 130, 246])  # 青背景
            else:
                if dist < min(w,h)//2 - 1:
                    raw_data += bytes([r, g, b_val])
                else:
                    raw_data += bytes([r, g, b_val])  # 角は同色
    
    compressed = zlib.compress(bytes(raw_data), 9)
    png += write_chunk(b'IDAT', compressed)
    png += write_chunk(b'IEND', b'')
    
    with open(path, 'wb') as f:
        f.write(png)

if __name__ == '__main__':
    resources_dir = os.path.join(os.path.dirname(__file__), 'Resources')
    os.makedirs(resources_dir, exist_ok=True)
    
    # アプリアイコン (赤: YouTubeカラー)
    create_minimal_ico(os.path.join(resources_dir, 'app.ico'), 32, (220, 38, 38))
    print("✓ app.ico 生成")
    
    # 警告アイコン (黄)
    create_minimal_ico(os.path.join(resources_dir, 'app_warn.ico'), 32, (217, 119, 6))
    print("✓ app_warn.ico 生成")
    
    # トレイアイコン (青)
    create_minimal_png(os.path.join(resources_dir, 'app_tray.png'), 64, (59, 130, 246))
    print("✓ app_tray.png 生成")
    
    # トレイ警告アイコン (警告)
    create_minimal_png(os.path.join(resources_dir, 'app_tray_warn.png'), 64, warn=True)
    print("✓ app_tray_warn.png 生成")
    
    print("\nアイコンリソースの生成が完了しました。")
    print("本番環境では適切なデザインのアイコンに差し替えてください。")
