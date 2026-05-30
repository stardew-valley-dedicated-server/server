<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import { Icon } from '@iconify/vue'

const props = withDefaults(defineProps<{
  images: { src: string; alt: string; source: string }[]
  initialIndex?: number
}>(), {
  initialIndex: 0,
})

const emit = defineEmits<{
  close: []
}>()

const currentIndex = ref(props.initialIndex)
const loaded = ref(false)

const currentSource = computed(() => props.images[currentIndex.value]?.source ?? '')

watch(() => props.initialIndex, (val) => {
  currentIndex.value = val
  loaded.value = false
})

function prev() {
  const len = props.images.length
  if (len === 0) return
  currentIndex.value = currentIndex.value > 0 ? currentIndex.value - 1 : len - 1
  loaded.value = false
}

function next() {
  const len = props.images.length
  if (len === 0) return
  currentIndex.value = currentIndex.value < len - 1 ? currentIndex.value + 1 : 0
  loaded.value = false
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape') {
    e.preventDefault()
    emit('close')
  } else if (e.key === 'ArrowLeft') {
    e.preventDefault()
    prev()
  } else if (e.key === 'ArrowRight') {
    e.preventDefault()
    next()
  }
}

onMounted(() => window.addEventListener('keydown', onKeydown))
onUnmounted(() => window.removeEventListener('keydown', onKeydown))
</script>

<template>
  <Teleport to="body">
    <div data-lightbox
         class="fixed inset-0 z-50 flex items-center justify-center bg-black/80 backdrop-blur-sm cursor-pointer"
         @click="emit('close')">
      <!-- Loading spinner -->
      <span v-if="!loaded" class="loading loading-ring loading-lg text-white/30 absolute" />
      <img v-if="images[currentIndex]"
           :src="images[currentIndex].src"
           :alt="images[currentIndex].alt"
           class="max-w-[95vw] max-h-[95vh] object-contain rounded-lg shadow-2xl transition-opacity duration-200"
           :class="loaded ? 'opacity-100' : 'opacity-0'"
           @load="loaded = true"
           @click.stop />
      <!-- Nav arrows -->
      <button v-if="images.length > 1"
              class="absolute left-2 top-1/2 -translate-y-1/2 p-2 rounded-full bg-black/30 text-white/50 hover:text-white hover:bg-black/50 transition-colors"
              @click.stop="prev">
        <Icon icon="lucide:chevron-left" class="w-6 h-6" />
      </button>
      <button v-if="images.length > 1"
              class="absolute right-2 top-1/2 -translate-y-1/2 p-2 rounded-full bg-black/30 text-white/50 hover:text-white hover:bg-black/50 transition-colors"
              @click.stop="next">
        <Icon icon="lucide:chevron-right" class="w-6 h-6" />
      </button>
      <!-- Counter + source label -->
      <div v-if="images.length > 1"
           class="absolute bottom-4 left-1/2 -translate-x-1/2 flex items-center gap-3">
        <span class="text-white/50 text-sm tabular-nums">
          {{ currentIndex + 1 }} / {{ images.length }}
        </span>
        <span v-if="currentSource" class="text-[10px] uppercase tracking-wider text-white/40 font-semibold">
          {{ currentSource }}
        </span>
      </div>
      <div v-else-if="currentSource"
           class="absolute bottom-4 left-1/2 -translate-x-1/2">
        <span class="text-[10px] uppercase tracking-wider text-white/40 font-semibold">
          {{ currentSource }}
        </span>
      </div>
      <button class="absolute top-4 right-4 text-white/70 hover:text-white transition-colors"
              @click="emit('close')">
        <Icon icon="lucide:x" class="w-8 h-8" />
      </button>
    </div>
  </Teleport>
</template>
