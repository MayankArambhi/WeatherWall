"""
AI Wallpaper Analyzer using CLIP embeddings
Analyzes ALL 40 conditions and finds the BEST image for each condition
Algorithm: FOR EACH CONDITION → Find best matching image
"""

import os
import json
import subprocess
import sys
from pathlib import Path

def main():
    print("\n" + "="*70)
    print("CLIP WALLPAPER ANALYSIS - CONDITION-BASED ASSIGNMENT")
    print("="*70)
    
    # Install required packages
    print("\nInstalling required packages...")
    try:
        subprocess.check_call([sys.executable, "-m", "pip", "install", "-q", 
                             "sentence-transformers", "numpy", "Pillow"])
    except Exception as e:
        print(f"✗ Failed to install packages: {e}")
        return

    try:
        from sentence_transformers import SentenceTransformer
        import numpy as np
        from PIL import Image
        
        # Get wallpaper folder from config
        script_dir = os.path.dirname(os.path.abspath(__file__))
        config_path = os.path.join(script_dir, "config.json")
        
        wallpaper_folder = None
        if os.path.exists(config_path):
            try:
                with open(config_path, 'r') as f:
                    config = json.load(f)
                    wallpaper_folder = config.get("WallpaperFolderPath", "")
            except Exception as e:
                print(f"✗ Error reading config.json: {e}")
                return
        
        if not wallpaper_folder or not os.path.exists(wallpaper_folder):
            print("\n✗ ERROR: Wallpaper folder not found in config.json")
            print("  Please set WallpaperFolderPath in config.json first")
            return
        
        print(f"\nWallpaper folder: {wallpaper_folder}")
        
        # Load CLIP model
        print("Loading CLIP model (ViT-B-32)...")
        model = SentenceTransformer('clip-ViT-B-32')
        print("✓ Model loaded\n")
        
        # Load text embeddings (40 conditions)
        embeddings_path = os.path.join(script_dir, "weather_embeddings.json")
        if not os.path.exists(embeddings_path):
            print(f"\n✗ ERROR: weather_embeddings.json not found")
            print(f"  Run generate_embeddings.py first")
            return
        
        print("Loading 40 condition embeddings...")
        with open(embeddings_path, 'r') as f:
            embeddings_data = json.load(f)
        
        # Extract text embeddings for all 40 conditions
        text_embeddings = {}
        all_conditions = []
        for condition, data in embeddings_data["conditions"].items():
            text_embeddings[condition] = np.array(data["embedding"])
            all_conditions.append(condition)
        
        print(f"✓ Loaded {len(text_embeddings)} condition embeddings\n")
        
        # Find and sort wallpaper files
        supported_formats = {'.jpg', '.jpeg', '.png', '.bmp', '.webp'}
        wallpaper_files = sorted([
            f for f in os.listdir(wallpaper_folder)
            if os.path.isfile(os.path.join(wallpaper_folder, f)) and 
               Path(f).suffix.lower() in supported_formats
        ])
        
        if not wallpaper_files:
            print(f"✗ No wallpaper images found in {wallpaper_folder}")
            return
        
        print(f"Found {len(wallpaper_files)} wallpaper images")
        print(f"\nPre-encoding all images...\n")
        
        # Pre-encode all images once (cache them)
        image_embeddings = {}
        failed_images = []
        
        for idx, filename in enumerate(wallpaper_files, 1):
            filepath = os.path.join(wallpaper_folder, filename)
            print(f"[{idx:2d}/{len(wallpaper_files)}] Encoding: {filename:45s}", end=" ", flush=True)
            
            try:
                # Load and preprocess image
                img = Image.open(filepath)
                if img.mode != 'RGB':
                    img = img.convert('RGB')
                
                orig_size = img.size
                
                # Resize to 224x224
                img = img.resize((224, 224), Image.Resampling.LANCZOS)
                
                # Encode with CLIP
                img_embedding = model.encode(img, convert_to_numpy=True)
                img_embedding = img_embedding / np.linalg.norm(img_embedding)  # Normalize
                
                image_embeddings[filename] = img_embedding
                print(f"✓ ({orig_size[0]}×{orig_size[1]})")
                
            except Exception as e:
                error_msg = str(e)
                print(f"✗ FAILED: {error_msg[:50]}")
                failed_images.append((filename, error_msg))
        
        if len(image_embeddings) == 0:
            print("\n✗ ERROR: No images could be encoded!")
            return
        
        print(f"\n✓ Successfully encoded {len(image_embeddings)} images")
        if failed_images:
            print(f"⚠ Failed to encode {len(failed_images)} images\n")
        
        # MAIN ALGORITHM: For each condition, find the BEST matching image
        print("\n" + "="*70)
        print("MATCHING IMAGES TO CONDITIONS")
        print("="*70)
        print(f"Analyzing {len(all_conditions)} conditions...\n")
        
        analysis = {}
        
        for cond_idx, condition in enumerate(all_conditions, 1):
            text_embedding = text_embeddings[condition]
            
            best_image = None
            best_score = -999
            all_scores = {}
            
            # Find best image for this condition
            for filename, img_embedding in image_embeddings.items():
                # Calculate cosine similarity
                similarity = float(np.dot(img_embedding, text_embedding))
                # Convert [-1, 1] to [0, 100]%
                confidence = max(0, min(100, (similarity + 1) * 50))
                
                all_scores[filename] = confidence
                
                if confidence > best_score:
                    best_score = confidence
                    best_image = filename
            
            # Rank all images by confidence for this condition
            ranked = sorted(all_scores.items(), key=lambda x: x[1], reverse=True)
            top_3 = ranked[:3]
            
            analysis[condition] = {
                "best_image": best_image,
                "best_confidence": round(best_score, 1),
                "top_3": [(img, round(score, 1)) for img, score in top_3],
                "all_scores": {img: round(score, 1) for img, score in ranked}
            }
            
            # Display progress
            status = "✓" if best_score >= 65 else "⚠"
            print(f"[{cond_idx:2d}/40] {condition:35s} → {best_image:30s} ({best_score:5.1f}%) {status}")
        
        # Save results
        output_path = os.path.join(script_dir, "wallpaper_analysis.json")
        output_data = {
            "model": "clip-ViT-B-32",
            "version": 2,
            "algorithm": "condition-based-assignment",
            "total_images": len(wallpaper_files),
            "successfully_encoded": len(image_embeddings),
            "failed_images": len(failed_images),
            "total_conditions": len(all_conditions),
            "analysis": analysis
        }
        
        with open(output_path, 'w') as f:
            json.dump(output_data, f, indent=2)
        
        # Summary statistics
        print("\n" + "="*70)
        print("ANALYSIS COMPLETE")
        print("="*70)
        print(f"Total conditions:       {len(all_conditions)}")
        print(f"Images encoded:         {len(image_embeddings)}")
        print(f"Failed to encode:       {len(failed_images)}")
        
        needs_review = sum(1 for r in analysis.values() if r["best_confidence"] < 65)
        high_confidence = sum(1 for r in analysis.values() if r["best_confidence"] >= 85)
        
        print(f"\nConfidence Summary:")
        print(f"  ✓ High (≥85%):        {high_confidence}")
        print(f"  ~ Medium (65-85%):    {len(analysis) - needs_review - high_confidence}")
        print(f"  ⚠ Low (<65%):         {needs_review}")
        
        print(f"\n✓ Results saved to: {output_path}")
        print("="*70 + "\n")
        
        if failed_images:
            print("Failed to encode:\n")
            for fname, error in failed_images:
                print(f"  ✗ {fname}")
                print(f"    → {error}\n")
        
    except Exception as e:
        print(f"\n✗ FATAL ERROR: {e}")
        import traceback
        traceback.print_exc()

if __name__ == "__main__":
    main()