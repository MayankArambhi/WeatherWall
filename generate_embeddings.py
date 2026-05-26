import os
import json
import subprocess
import sys

# Generate text embeddings for all 40 WeatherWall conditions (10 weather × 4 time periods)
# Each prompt is carefully crafted to match actual wallpaper image characteristics
prompts = {
    # CLEAR SKY CONDITIONS
    "clear_morning": "bright sunny morning with clear blue sky and strong golden sunlight, sunrise colors, golden hour",
    "clear_afternoon": "bright sunny afternoon with intense blue sky, strong sunlight, clear visibility, vibrant colors",
    "clear_evening": "clear evening with warm golden sunset colors, orange and pink sky, golden hour light, sunset glow",
    "clear_night": "clear night sky with stars, moonlight, dark sky with celestial bodies, starry night, lunar light",
    
    # PARTLY CLOUDY CONDITIONS
    "partly_cloudy_morning": "partly cloudy morning with some clouds and patches of blue sky, soft sunlight, morning clouds",
    "partly_cloudy_afternoon": "partly cloudy afternoon with mix of clouds and sun, dappled light, variable brightness",
    "partly_cloudy_evening": "partly cloudy evening with clouds at sunset, warm light through clouds, mixed light",
    "partly_cloudy_night": "partly cloudy night with some stars visible, clouds moving across sky, lunar light on clouds",
    
    # CLOUDY CONDITIONS
    "cloudy_morning": "cloudy morning with grey overcast sky, soft diffuse light, no direct sunlight, muted colors",
    "cloudy_afternoon": "cloudy afternoon with full cloud cover, grey sky, flat diffuse lighting, no shadows",
    "cloudy_evening": "cloudy evening with overcast sky, dark clouds, reduced sunlight, gloomy conditions",
    "cloudy_night": "cloudy night with dark overcast sky, no stars visible, very dark conditions, clouds covering sky",
    
    # OVERCAST CONDITIONS
    "overcast_morning": "overcast gloomy morning with heavy dark clouds, low visibility, very dark conditions, oppressive sky",
    "overcast_afternoon": "overcast dark afternoon with heavy cloud cover, minimal light, very gloomy, threatening weather",
    "overcast_evening": "overcast evening with very dark clouds, minimal sunset light, gloomy atmosphere, dark sky",
    "overcast_night": "overcast dark night with heavy clouds, very low visibility, pitch black conditions, no light",
    
    # RAINY CONDITIONS
    "rainy_morning": "rainy morning with rain, wet surfaces, puddles, raindrops, overcast wet weather, grey conditions",
    "rainy_afternoon": "rainy afternoon with active rain, wet ground, dark clouds, rainfall visible, wet atmosphere",
    "rainy_evening": "rainy evening with rain and wet conditions, rain in golden/orange light, wet surfaces, moody",
    "rainy_night": "rainy night with rain, wet surfaces, dark rainy conditions, water, reflections in puddles",
    
    # DRIZZLE CONDITIONS
    "drizzle_morning": "light drizzle morning with mist and fine rain, misty atmosphere, morning mist, light precipitation",
    "drizzle_afternoon": "light drizzle afternoon with fine rain and mist, hazy conditions, light precipitation, misty",
    "drizzle_evening": "light drizzle evening with fine rain and mist, evening mist, gentle precipitation, hazy light",
    "drizzle_night": "light drizzle night with mist and fine rain, misty dark conditions, gentle precipitation at night",
    
    # THUNDERSTORM CONDITIONS
    "thunderstorm_morning": "severe thunderstorm morning with dark dramatic clouds, lightning, heavy rain, storm conditions",
    "thunderstorm_afternoon": "severe thunderstorm afternoon with dramatic storm clouds, lightning bolts, heavy precipitation, violent",
    "thunderstorm_evening": "severe thunderstorm evening with dark storm clouds, lightning, heavy rain, dramatic weather",
    "thunderstorm_night": "severe thunderstorm night with lightning in dark sky, thunder clouds, heavy rain, violent storm",
    
    # FOGGY CONDITIONS
    "foggy_morning": "foggy misty morning with heavy fog, low visibility, mist, haze, obscured landscape, fog layers",
    "foggy_afternoon": "foggy afternoon with fog and mist, reduced visibility, hazy atmosphere, foggy landscape",
    "foggy_evening": "foggy evening with fog and mist, fog in golden light, misty atmosphere, evening haze",
    "foggy_night": "foggy night with heavy fog and mist, very low visibility, dark misty conditions, fog at night",
    
    # SNOWY CONDITIONS
    "snowy_morning": "snowy morning with fresh snow, white landscape, winter conditions, snow on ground, cold atmosphere",
    "snowy_afternoon": "snowy afternoon with snow on landscape, white snow cover, winter daylight, cold clear snow",
    "snowy_evening": "snowy evening with snow in golden/orange light, winter sunset, snowy landscape at dusk",
    "snowy_night": "snowy night with snow under moonlight, white snow cover at night, cold winter night, lunar snow",
    
    # WINDY CONDITIONS
    "windy_morning": "windy morning with wind effects, moving trees, wind-blown elements, bending vegetation, air movement",
    "windy_afternoon": "windy afternoon with strong wind, blowing trees, wind-blown clouds, dynamic weather, air turbulence",
    "windy_evening": "windy evening with wind, blowing trees and vegetation, wind-swept landscape, evening wind",
    "windy_night": "windy night with wind effects, night wind, moving trees, windy dark conditions, air movement at night"
}

def main():
    print("Installing required packages...")
    try:
        subprocess.check_call([sys.executable, "-m", "pip", "install", "-q", "sentence-transformers", "numpy", "Pillow"])
    except Exception as e:
        print(f"Failed to install packages: {e}")
        return

    print("\n" + "="*70)
    print("CLIP TEXT EMBEDDING GENERATION")
    print("="*70)
    print(f"Loading CLIP model (ViT-B-32)...")
    print(f"Total conditions to embed: {len(prompts)} (10 weather × 4 time periods)\n")
    
    try:
        from sentence_transformers import SentenceTransformer
        import numpy as np
        
        # Load CLIP model
        model = SentenceTransformer('clip-ViT-B-32')
        print(f"Model loaded successfully\n")
        
        embeddings = {}
        print("Encoding text prompts for all 40 conditions:\n")
        
        # Group by weather type for organized output
        weather_types = ["clear", "partly_cloudy", "cloudy", "overcast", "rainy", 
                        "drizzle", "thunderstorm", "foggy", "snowy", "windy"]
        
        for weather in weather_types:
            print(f"[{weather.upper()}]")
            for time in ["morning", "afternoon", "evening", "night"]:
                condition = f"{weather}_{time}"
                prompt = prompts[condition]
                
                # Encode and normalize
                emb = model.encode(prompt, convert_to_numpy=True)
                emb = emb / np.linalg.norm(emb)  # L2 normalize
                
                embeddings[condition] = {
                    "embedding": emb.tolist(),
                    "prompt": prompt
                }
                print(f"  ✓ {condition:30s} → {prompt[:50]}...")
            print()
        
        # Save embeddings
        output_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "weather_embeddings.json")
        output_data = {
            "model": "clip-ViT-B-32",
            "version": 2,
            "conditions": embeddings,
            "embedding_dimension": len(embeddings["clear_morning"]["embedding"])
        }
        
        with open(output_path, "w") as f:
            json.dump(output_data, f, indent=2)
        
        print("="*70)
        print(f"✓ SUCCESS: Generated {len(embeddings)} text embeddings")
        print(f"✓ Saved to: {output_path}")
        print(f"✓ Embedding dimension: {output_data['embedding_dimension']}")
        print("="*70 + "\n")
        
    except Exception as e:
        print(f"✗ ERROR generating embeddings: {e}")
        import traceback
        traceback.print_exc()

if __name__ == "__main__":
    main()
