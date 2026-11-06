"""Render public Mermaid sources using Kroki; requires Python and Pillow."""
from pathlib import Path
import urllib.request
from io import BytesIO
from PIL import Image

def main():
    root = Path(__file__).resolve().parent
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    for source in sorted(root.glob("*.mmd")):
        request = urllib.request.Request(
            "https://kroki.io/mermaid/png", data=source.read_bytes(),
            headers={"Content-Type": "text/plain; charset=utf-8", "User-Agent": "SeaPower-docs"})
        with opener.open(request, timeout=60) as response:
            data = response.read()
        if not data.startswith(b"\x89PNG\r\n\x1a\n"):
            raise ValueError("Renderer did not return PNG: " + source.name)
        image = Image.open(BytesIO(data)).convert("RGBA")
        background = Image.new("RGBA", image.size, "white")
        background.alpha_composite(image)
        background.convert("RGB").save(source.with_suffix(".png"))
        print(source.with_suffix(".png").name, len(data))

if __name__ == "__main__":
    main()
