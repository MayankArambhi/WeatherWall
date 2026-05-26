import os
import json
import subprocess
import sys

# List of weather conditions to embed
prompts = {
    "clear": "a photo of a clear bright sunny day with blue sky",
    "partly_cloudy": "a photo of a partly cloudy sky with some sun",
    "cloudy": "a photo of a cloudy day with grey skies",
    "overcast": "a photo of a dark overcast gloomy day",
    "rainy": "a photo of a rainy wet day with rain drops",
    "drizzle": "a photo of light drizzle rain mist",
    "thunderstorm": "a photo of a severe thunderstorm with lightning and dark stormy clouds",
    "foggy": "a photo of a foggy misty landscape with low visibility",
    "snowy": "a photo of a snowy winter day with snow on the ground",
    "windy": "a photo of a windy day with trees bending in the wind"
}

def main():
    print("Installing sentence-transformers and numpy...")
    try:
        subprocess.check_call([sys.executable, "-m", "pip", "install", "sentence-transformers", "numpy"])
    except Exception as e:
        print(f"Failed to install package: {e}")
        return

    print("Loading CLIP model and generating text embeddings (this will download ~200MB model on first run)...")
    try:
        from sentence_transformers import SentenceTransformer
        # sentence-transformers uses clip-ViT-B-32
        model = SentenceTransformer('clip-ViT-B-32')
        
        embeddings = {}
        for weather, prompt in prompts.items():
            print(f"Encoding prompt for '{weather}': '{prompt}'")
            emb = model.encode(prompt)
            # Normalize vector to unit length so dot product directly equals cosine similarity
            import numpy as np
            emb = emb / np.linalg.norm(emb)
            embeddings[weather] = emb.tolist()
            
        output_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "weather_embeddings.json")
        with open(output_path, "w") as f:
            json.dump(embeddings, f, indent=4)
            
        print(f"\nSuccess! Saved weather text embeddings to: {output_path}")
    except Exception as e:
        print(f"Error generating embeddings: {e}")

if __name__ == "__main__":
    main()
