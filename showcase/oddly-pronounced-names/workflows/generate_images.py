"""Generates the video's scene backgrounds with Qwen Image 2.1 on a ComfyUI server.

Usage: python generate_images.py [scene ...]   (all scenes if none given)
Set COMFYUI_URL to point at the server (default http://127.0.0.1:8188).
Images are written to ../images/<scene>.jpg; existing images are skipped.
"""
import io
import json
import os
import pathlib
import sys
import time
import urllib.parse
import urllib.request

from PIL import Image

HERE = pathlib.Path(__file__).parent
OUT = HERE.parent / "images"
SERVER = os.environ.get("COMFYUI_URL", "http://127.0.0.1:8188").rstrip("/")

STYLE = (
    " Painted in a rich, vibrant cinematic illustration style like a vintage British railway travel poster "
    "crossed with modern digital matte painting: bold saturated colours, dramatic golden-hour light, deep "
    "teal shadows, crisp detail, strong depth. Absolutely no text, no letters, no words, no signage, "
    "no logos, no watermark."
)
NO_TEXT = " Absolutely no text, no letters, no words, no logos, no watermark."

SCENES = {
    "intro": "A wide view of a quintessential English village green at sunset: an old wooden fingerpost signpost "
             "with many blank arms pointing in different directions stands in the foreground, a red telephone box, "
             "a thatched pub, a stone church spire and rolling green hills beyond under a blazing orange and purple sky.",
    "leominster": "A wide view of a historic English market town street in Herefordshire lined with crooked black-and-white "
                  "Tudor timber-framed buildings, an old priory church tower at the end of the street, hanging flower baskets, "
                  "cobbles glowing in warm evening light.",
    "beauchamp": "A wide view of a mighty medieval English castle with tall stone towers and battlements rising above a calm "
                 "river, banners fluttering, a lush green meadow in the foreground, dramatic clouds lit gold by the setting sun.",
    "mousehole": "A wide view of a tiny Cornish fishing harbour at dusk: granite cottages tumbling down the hillside, little "
                 "wooden fishing boats in the curved stone harbour, strings of glowing festive lights reflected in the calm "
                 "water, a deep blue twilight sky.",
    "menzies": "A wide view of a sixteenth-century white-harled Scottish tower house castle with turrets and crow-stepped gables, "
               "set against misty purple heather-clad Highland mountains, ancient pine trees, moody dramatic light breaking "
               "through clouds.",
    "belvoir": "A wide view of a romantic Gothic-revival English castle with towers and turrets crowning a wooded hilltop, "
               "overlooking a patchwork vale of green fields and hedgerows, golden sunrise mist in the valley.",
    "marjoribanks": "A wide view of three happy golden retriever dogs bounding through a Scottish Highland glen beside a "
                    "Victorian hunting lodge, golden autumn bracken, a sparkling river, mountains and dramatic warm light.",
    "happisburgh": "A wide view of a tall red-and-white horizontally striped lighthouse standing on green clifftops above a "
                   "wide sandy Norfolk beach, a medieval flint church tower nearby, a huge bright blue sky with towering "
                   "white clouds, waves rolling in.",
    "cholmondeley": "A wide view of a grand castellated English stately home with towers and battlements, set in landscaped "
                    "parkland gardens with a lake, ornamental bridges and blossoming trees, peacocks on the lawn, warm "
                    "afternoon light.",
    "woolfardisworthy": "A wide view of a sleepy Devon village with whitewashed thatched cottages along a narrow winding "
                        "lane between high green hedgerows full of wildflowers, a church tower, sheep in rolling fields, "
                        "soft golden summer light.",
    "featherstonehaugh": "A comic, grand eighteenth-century oil-portrait style scene: a very pompous Georgian aristocrat in a "
                         "tall powdered wig, embroidered coat and lace cravat, nose in the air, standing in front of an elegant "
                         "red-brick country mansion on rolling downs, dramatic theatrical lighting.",
    "outro": "An abstract futuristic background: glowing neon audio waveforms in magenta and cyan flowing across a deep navy "
             "space, interwoven with luminous floating film-strip frames and soft bokeh particles, sleek and modern.",
}


def graph(prompt, seed, prefix):
    """Qwen Image 2.1 text-to-image in ComfyUI API format (no prompt enhancer)."""
    return {
        "1": {"class_type": "UNETLoader",
              "inputs": {"unet_name": "qwen_image_2.1_int8_convrot.safetensors", "weight_dtype": "default"}},
        "2": {"class_type": "QwenImage21Cache", "inputs": {"model": ["1", 0], "device": "auto", "dtype": "default"}},
        "3": {"class_type": "CLIPLoader",
              "inputs": {"clip_name": "qwen3vl_8b_int8_convrot.safetensors", "type": "qwen_image", "device": "default"}},
        "4": {"class_type": "TextEncodeQwenImage21",
              "inputs": {"clip": ["3", 0], "prompt": prompt, "negative_prompt": "", "resolution": 1024}},
        "5": {"class_type": "VAELoader", "inputs": {"vae_name": "qwen_image_2.1_vae_bf16.safetensors"}},
        "6": {"class_type": "EmptyLatentImage", "inputs": {"width": 1920, "height": 1088, "batch_size": 1}},
        "7": {"class_type": "KSampler",
              "inputs": {"model": ["2", 0], "positive": ["4", 0], "negative": ["4", 1], "latent_image": ["6", 0],
                         "seed": seed, "steps": 30, "cfg": 1, "sampler_name": "euler", "scheduler": "simple",
                         "denoise": 1}},
        "8": {"class_type": "VAEDecode", "inputs": {"samples": ["7", 0], "vae": ["5", 0]}},
        "9": {"class_type": "SaveImage", "inputs": {"images": ["8", 0], "filename_prefix": prefix}},
    }


def post(path, body):
    req = urllib.request.Request(SERVER + path, json.dumps(body).encode(), {"Content-Type": "application/json"})
    return json.load(urllib.request.urlopen(req))


def get(path):
    return urllib.request.urlopen(SERVER + path).read()


def main(names):
    OUT.mkdir(exist_ok=True)
    pending = {}
    for index, name in enumerate(SCENES):
        if names and name not in names:
            continue
        if (OUT / f"{name}.jpg").exists():
            print(f"{name}: exists, skipped")
            continue
        prompt = SCENES[name] + (NO_TEXT if name == "outro" else STYLE)
        result = post("/prompt", {"prompt": graph(prompt, 1000 + index, f"names_{name}")})
        if result.get("node_errors"):
            sys.exit(f"{name}: {result['node_errors']}")
        pending[result["prompt_id"]] = name
        print(f"{name}: queued {result['prompt_id']}")

    while pending:
        time.sleep(5)
        for prompt_id, name in list(pending.items()):
            history = json.loads(get(f"/history/{prompt_id}")).get(prompt_id)
            if not history:
                continue
            status = history.get("status", {})
            if status.get("status_str") == "error":
                print(f"{name}: FAILED {status.get('messages')}")
                del pending[prompt_id]
                continue
            for output in history.get("outputs", {}).values():
                for image in output.get("images", []):
                    query = urllib.parse.urlencode(image)
                    Image.open(io.BytesIO(get(f"/view?{query}"))).convert("RGB").save(OUT / f"{name}.jpg", quality=90)
                    print(f"{name}: saved")
            del pending[prompt_id]


if __name__ == "__main__":
    main(set(sys.argv[1:]))
