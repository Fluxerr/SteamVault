import urllib.request, json

r = urllib.request.urlopen('https://store.steampowered.com/api/featuredcategories?l=english&cc=US')
d = json.loads(r.read())

for cat in ['top_sellers', 'new_releases', 'specials', 'coming_soon']:
    print(f"\n=== {cat} ===")
    items = d.get(cat, {}).get('items', [])
    print(f"Total items: {len(items)}")
    for i, item in enumerate(items[:15]):
        typ = item.get('type', 'MISSING')
        name = str(item.get('name', '?'))[:60]
        images = [k for k in ['header_image','capsule_image','tiny_image'] if item.get(k)]
        print(f"  [{i}] id={item.get('id')}, type={typ} ({type(typ).__name__}), name={name}, images={images}")
        # Show type breakdown
    types = {}
    for item in items:
        t = item.get('type', 'MISSING')
        types[t] = types.get(t, 0) + 1
    print(f"Type breakdown: {types}")