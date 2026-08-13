"""Render PDF pages as WebP images for the offline HTML5 reader."""
import os
import sys
import fitz
from PIL import Image


def main():
    source_path, output_directory = sys.argv[1], sys.argv[2]
    start_page = int(sys.argv[3])
    end_page = int(sys.argv[4])
    pages_directory = os.path.join(output_directory, "pages")
    os.makedirs(pages_directory, exist_ok=True)
    document = fitz.open(source_path)
    try:
        matrix = fitz.Matrix(1.5, 1.5)
        for page_number in range(start_page, end_page + 1):
            page = document[page_number - 1]
            pixmap = page.get_pixmap(matrix=matrix, alpha=False)
            try:
                image = Image.frombytes("RGB", (pixmap.width, pixmap.height), pixmap.samples)
                try:
                    image.save(
                        os.path.join(pages_directory, "page-{:04d}.webp".format(page_number)),
                        "WEBP",
                        quality=82,
                        method=2,
                    )
                finally:
                    image.close()
            finally:
                pixmap = None
    finally:
        document.close()


if __name__ == "__main__":
    main()
